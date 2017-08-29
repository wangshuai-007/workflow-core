﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using System.Reflection;
using WorkflowCore.Exceptions;

namespace WorkflowCore.Services
{
    public class WorkflowHost : IWorkflowHost, IDisposable
    {                
        protected bool _shutdown = true;        
        protected IServiceProvider _serviceProvider;

        private readonly IEnumerable<IBackgroundTask> _backgroundTasks;

        public event StepErrorEventHandler OnStepError;

        // Public dependencies to allow for extension method access.
        public IPersistenceProvider PersistenceStore { get; private set; }
        public IDistributedLockProvider LockProvider { get; private set; }
        public IWorkflowRegistry Registry { get; private set; }
        public WorkflowOptions Options { get; private set; }
        public IQueueProvider QueueProvider { get; private set; }
        public ILogger Logger { get; private set; }

        public WorkflowHost(IPersistenceProvider persistenceStore, IQueueProvider queueProvider, WorkflowOptions options, ILoggerFactory loggerFactory, IServiceProvider serviceProvider, IWorkflowRegistry registry, IDistributedLockProvider lockProvider, IEnumerable<IBackgroundTask> backgroundTasks)
        {
            PersistenceStore = persistenceStore;
            QueueProvider = queueProvider;
            Options = options;
            Logger = loggerFactory.CreateLogger<WorkflowHost>();
            _serviceProvider = serviceProvider;
            Registry = registry;
            LockProvider = lockProvider;
            _backgroundTasks = backgroundTasks;
            persistenceStore.EnsureStoreExists();
        }

        public Task<string> StartWorkflow(string workflowId, object data = null)
        {
            return StartWorkflow(workflowId, null, data);
        }

        public Task<string> StartWorkflow(string workflowId, int? version, object data = null)
        {
            return StartWorkflow<object>(workflowId, version, data);
        }

        public Task<string> StartWorkflow<TData>(string workflowId, TData data = null) 
            where TData : class
        {
            return StartWorkflow<TData>(workflowId, null, data);
        }

        public async Task<string> StartWorkflow<TData>(string workflowId, int? version, TData data = null)
            where TData : class
        {
            if (_shutdown)
            {
                throw new InvalidOperationException("Host is not running");
            }

            var def = Registry.GetDefinition(workflowId, version);
            if (def == null)
            {
                throw new WorkflowNotRegisteredException(workflowId, version);
            }

            var wf = new WorkflowInstance
            {
                WorkflowDefinitionId = workflowId,
                Version = def.Version,
                Data = data,
                Description = def.Description,
                NextExecution = 0,
                CreateTime = DateTime.Now.ToUniversalTime(),
                Status = WorkflowStatus.Runnable
            };

            if ((def.DataType != null) && (data == null))
            {
                wf.Data = def.DataType.GetConstructor(new Type[] { }).Invoke(null);
            }

            wf.ExecutionPointers.Add(new ExecutionPointer
            {
                Id = Guid.NewGuid().ToString(),
                StepId = 0,
                Active = true,
                StepName = def.Steps.First(x => x.Id == 0).Name
            });

            string id = await PersistenceStore.CreateNewWorkflow(wf);
            await QueueProvider.QueueWork(id, QueueType.Workflow);
            return id;
        }

        public void Start()
        {            
            _shutdown = false;
            PersistenceStore.EnsureStoreExists();
            QueueProvider.Start().Wait();
            LockProvider.Start().Wait();

            Logger.LogInformation("Starting backgroud tasks");

            foreach (var task in _backgroundTasks)
                task.Start();
        }

        public void Stop()
        {
            _shutdown = true;            
            
            Logger.LogInformation("Stopping background tasks");
            foreach (var th in _backgroundTasks)
                th.Stop();
            
            Logger.LogInformation("Worker tasks stopped");
            
            QueueProvider.Stop();
            LockProvider.Stop();
        }
        
        public async Task PublishEvent(string eventName, string eventKey, object eventData, DateTime? effectiveDate = null)
        {
            if (_shutdown)
            {
                throw new InvalidOperationException("Host is not running");
            }

            Logger.LogDebug("Creating event {0} {1}", eventName, eventKey);
            Event evt = new Event();

            if (effectiveDate.HasValue)
                evt.EventTime = effectiveDate.Value.ToUniversalTime();
            else
                evt.EventTime = DateTime.Now.ToUniversalTime();

            evt.EventData = eventData;
            evt.EventKey = eventKey;
            evt.EventName = eventName;
            evt.IsProcessed = false;
            string eventId = await PersistenceStore.CreateEvent(evt);

            await QueueProvider.QueueWork(eventId, QueueType.Event);
        }
                
        public void RegisterWorkflow<TWorkflow>() 
            where TWorkflow : IWorkflow, new()
        {
            TWorkflow wf = new TWorkflow();
            Registry.RegisterWorkflow(wf);
        }

        public void RegisterWorkflow<TWorkflow, TData>() 
            where TWorkflow : IWorkflow<TData>, new()
            where TData : new()
        {
            TWorkflow wf = new TWorkflow();
            Registry.RegisterWorkflow<TData>(wf);
        }

        public async Task<bool> SuspendWorkflow(string workflowId)
        {
            if (await LockProvider.AcquireLock(workflowId, new CancellationToken()))
            {
                try
                {
                    var wf = await PersistenceStore.GetWorkflowInstance(workflowId);
                    if (wf.Status == WorkflowStatus.Runnable)
                    {
                        wf.Status = WorkflowStatus.Suspended;
                        await PersistenceStore.PersistWorkflow(wf);
                        return true;
                    }

                    return false;
                }
                finally
                {
                    await LockProvider.ReleaseLock(workflowId);
                }
            }

            return false;
        }

        public async Task<bool> ResumeWorkflow(string workflowId)
        {
            if (await LockProvider.AcquireLock(workflowId, new CancellationToken()))
            {
                bool requeue = false;
                try
                {
                    var wf = await PersistenceStore.GetWorkflowInstance(workflowId);
                    if (wf.Status == WorkflowStatus.Suspended)
                    {
                        wf.Status = WorkflowStatus.Runnable;
                        await PersistenceStore.PersistWorkflow(wf);
                        requeue = true;
                        return true;
                    }

                    return false;
                }
                finally
                {
                    await LockProvider.ReleaseLock(workflowId);
                    if (requeue)
                        await QueueProvider.QueueWork(workflowId, QueueType.Workflow);
                }                
            }

            return false;
        }

        public async Task<bool> TerminateWorkflow(string workflowId)
        {
            if (await LockProvider.AcquireLock(workflowId, new CancellationToken()))
            {
                try
                {
                    var wf = await PersistenceStore.GetWorkflowInstance(workflowId);                    
                    wf.Status = WorkflowStatus.Terminated;
                    await PersistenceStore.PersistWorkflow(wf);
                    return true;                    
                }
                finally
                {
                    await LockProvider.ReleaseLock(workflowId);
                }
            }

            return false;
        }


        public void ReportStepError(WorkflowInstance workflow, WorkflowStep step, Exception exception)
        {
            OnStepError?.Invoke(workflow, step, exception);
        }

        public void Dispose()
        {
            if (!_shutdown)
                Stop();
        }
    }
}
