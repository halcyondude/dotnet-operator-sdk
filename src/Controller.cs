﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Models;
using Microsoft.Rest;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace ContainerSolutions.OperatorSDK
{
    public class Controller<T> where T : BaseCRD
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public Kubernetes Kubernetes { get; private set; }

        private readonly IOperationHandler<T> m_handler;
        private readonly T m_crd;
        private Watcher<T> m_watcher;

        static Controller()
        {
            ConfigLogger();
        }

        static bool s_loggerConfiged = false;
        public static void ConfigLogger()
        {
            if (!s_loggerConfiged)
            {
                var config = new LoggingConfiguration();
                var consoleTarget = new ColoredConsoleTarget
                {
                    Name = "coloredConsole",
                    Layout = "${longdate} [${level:uppercase=true}] ${logger}:${message}",
                };
                config.AddRule(LogLevel.Debug, LogLevel.Fatal, consoleTarget, "*");
                LogManager.Configuration = config;

                s_loggerConfiged = true;
            }
        }

        public Controller(T crd, IOperationHandler<T> handler)
        {
            KubernetesClientConfiguration config;
            if (KubernetesClientConfiguration.IsInCluster())
            {
                config = KubernetesClientConfiguration.InClusterConfig();
            }
            else
            {
                config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
            }

            Kubernetes = new Kubernetes(config);
            m_crd = crd;
            m_handler = handler;
        }

        ~Controller()
        {
            DisposeWatcher();
        }

        public async Task SatrtAsync(string k8sNamespace = "")
        {
            Stopwatch watch = new Stopwatch();
            HttpOperationResponse<object> listResponse = null;
            while (listResponse == null)
            {
                try
                {
                    watch.Start();
                    listResponse = await Kubernetes.ListNamespacedCustomObjectWithHttpMessagesAsync(m_crd.Group, m_crd.Version, k8sNamespace, m_crd.Plural, watch: true);
                }
                catch (HttpOperationException hoex) when (hoex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Log.Warn($"No CustomResourceDefinition found for '{m_crd.Plural}', group '{m_crd.Group}' and version '{m_crd.Version}' on namespace '{k8sNamespace}'.");
                    Log.Info($"Checking again in {m_crd.ReconciliationCheckInterval} seconds...");
                    Thread.Sleep(m_crd.ReconciliationCheckInterval * 1000);
                }
                catch (TaskCanceledException)
                {
                    //this catch should be gone after issue #494 on the KubernetesClient gets fixed
                    //https://github.com/kubernetes-client/csharp/issues/494

                    watch.Stop();
                    Log.Info($"Listener timed out after {watch.Elapsed.TotalSeconds} seconds. Trying to reconnect.");
                    watch.Reset();
                    // just check again
                }
            }

            m_watcher = listResponse.Watch<T, object>(this.OnTChange, this.OnError);

            await ReconciliationLoop();
        }
        private Task ReconciliationLoop()
        {
            return Task.Run(() =>
            {
                Log.Info($"Reconciliation Loop for CRD {m_crd.Singular} will run every {m_crd.ReconciliationCheckInterval} seconds.");

                while (true)
                {
                    Thread.Sleep(m_crd.ReconciliationCheckInterval * 1000);

                    m_handler.CheckCurrentState(Kubernetes);
                }
            });
        }

        void DisposeWatcher()
        {
            if (m_watcher != null && m_watcher.Watching)
                m_watcher.Dispose();
        }

        private async void OnTChange(WatchEventType type, T item)
        {
            Log.Info($"{typeof(T)} {item.Name()} {type} on Namespace {item.Namespace()}");

            try
            {
                switch (type)
                {
                    case WatchEventType.Added:
                        if (m_handler != null)
                            await m_handler.OnAdded(Kubernetes, item);
                        return;
                    case WatchEventType.Modified:
                        if (m_handler != null)
                            await m_handler.OnUpdated(Kubernetes, item);
                        return;
                    case WatchEventType.Deleted:
                        if (m_handler != null)
                            await m_handler.OnDeleted(Kubernetes, item);
                        return;
                    case WatchEventType.Bookmark:
                        if (m_handler != null)
                            await m_handler.OnBookmarked(Kubernetes, item);
                        return;
                    case WatchEventType.Error:
                        if (m_handler != null)
                            await m_handler.OnError(Kubernetes, item);
                        return;
                    default:
                        Log.Warn($"Don't know what to do with {type}");
                        break;
                };
            }
            catch (Exception ex)
            {
                Log.Error($"An error occurred on the '{type}' call of {item.Name()} ({typeof(T)})");
                Log.Error(ex);
            }
        }

        private void OnError(Exception exception)
        {
            Log.Fatal(exception);
        }
    }
}
