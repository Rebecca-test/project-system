﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.GraphModel;
using Microsoft.VisualStudio.GraphModel.CodeSchema;
using Microsoft.VisualStudio.GraphModel.Schemas;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.GraphNodes.Actions;
using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.Models;
using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.Snapshot;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

using IAsyncServiceProvider = Microsoft.VisualStudio.Shell.IAsyncServiceProvider;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.GraphNodes
{
    /// <summary>
    /// Provides actual dependencies nodes under Dependencies\[DependencyType]\[TopLevel]\[....] sub nodes. 
    /// Note: when dependency has <see cref="ProjectTreeFlags.Common.BrokenReference"/> flag,
    /// <see cref="IGraphProvider"/> API are not called for that node.
    /// </summary>
    [Export(typeof(DependenciesGraphProvider))]
    [Export(typeof(IDependenciesGraphBuilder))]
    [AppliesTo(ProjectCapability.DependenciesTree)]
    internal class DependenciesGraphProvider : OnceInitializedOnceDisposedAsync, IGraphProvider, IDependenciesGraphBuilder
    {
        [ImportingConstructor]
        public DependenciesGraphProvider(IAggregateDependenciesSnapshotProvider aggregateSnapshotProvider,
                                         [Import(typeof(SAsyncServiceProvider))] IAsyncServiceProvider serviceProvider,
                                         JoinableTaskContext joinableTaskContext)
            : base(new JoinableTaskContextNode(joinableTaskContext))
        {
            AggregateSnapshotProvider = aggregateSnapshotProvider;
            ServiceProvider = serviceProvider;
            GraphActionHandlers = new OrderPrecedenceImportCollection<IDependenciesGraphActionHandler>(
                                    ImportOrderPrecedenceComparer.PreferenceOrder.PreferredComesLast);
        }

        private static readonly GraphCommand[] s_containsGraphCommand =
        {
            new GraphCommand(
                GraphCommandDefinition.Contains,
                targetCategories: null,
                linkCategories: new[] {GraphCommonSchema.Contains},
                trackChanges: true)
        };

        /// <summary>
        /// All icons that are used tree graph, register their monikers once to avoid extra UI thread switches.
        /// </summary>
        private ImmutableHashSet<ImageMoniker> _knownIcons = ImmutableHashSet<ImageMoniker>.Empty;

        [ImportMany]
        private OrderPrecedenceImportCollection<IDependenciesGraphActionHandler> GraphActionHandlers { get; }

        private readonly object _snapshotChangeHandlerLock = new object();
        private IVsImageService2 _imageService;
        private readonly object _expandedGraphContextsLock = new object();

        /// <summary>
        /// Remembers expanded graph nodes to track changes in their children.
        /// </summary>
        protected WeakCollection<IGraphContext> ExpandedGraphContexts { get; } = new WeakCollection<IGraphContext>();

        private IAggregateDependenciesSnapshotProvider AggregateSnapshotProvider { get; }

        private IAsyncServiceProvider ServiceProvider { get; }

        protected override async Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            AggregateSnapshotProvider.SnapshotChanged += OnSnapshotChanged;

            _imageService = (IVsImageService2)await ServiceProvider.GetServiceAsync(typeof(SVsImageService));
        }

        protected override Task DisposeCoreAsync(bool initialized)
        {
            AggregateSnapshotProvider.SnapshotChanged -= OnSnapshotChanged;

            return Task.CompletedTask;
        }

        /// <summary>
        /// IGraphProvider.BeginGetGraphData
        /// Entry point for progression. Gets called every time when progression
        ///  - Needs to know if a node has children
        ///  - Wants to get children for a node
        ///  - During solution explorer search
        /// </summary>
        public void BeginGetGraphData(IGraphContext context)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(() => BeginGetGraphDataAsync(context));
        }

        /// <summary>
        /// IGraphProvider.GetCommands
        /// </summary>
        public IEnumerable<GraphCommand> GetCommands(IEnumerable<GraphNode> nodes)
        {
            return s_containsGraphCommand;
        }

        /// <summary>
        /// IGraphProvider.GetExtension
        /// </summary>
        public T GetExtension<T>(GraphObject graphObject, T previous) where T : class
        {
            return null;
        }

        /// <summary>
        /// IGraphProvider.Schema
        /// </summary>
        public Graph Schema => null;

        internal async Task BeginGetGraphDataAsync(IGraphContext context)
        {
            try
            {
                await InitializeAsync();

                IEnumerable<Lazy<IDependenciesGraphActionHandler, IOrderPrecedenceMetadataView>> actionHandlers = GraphActionHandlers.Where(x => x.Value.CanHandleRequest(context));
                bool shouldTrackChanges = actionHandlers.Aggregate(
                    false, (previousTrackFlag, handler) => previousTrackFlag || handler.Value.HandleRequest(context));

                if (!shouldTrackChanges)
                {
                    return;
                }

                lock (_expandedGraphContextsLock)
                {
                    if (!ExpandedGraphContexts.Contains(context))
                    {
                        // Remember this graph context in order to track changes.
                        // When references change, we will adjust children of this graph as necessary
                        ExpandedGraphContexts.Add(context);
                    }
                }
            }
            finally
            {
                // OnCompleted must be called to display changes 
                context.OnCompleted();
            }
        }

        /// <summary>
        /// ProjectContextChanged gets fired every time dependencies change for projects across solution.
        /// ExpandedGraphContexts contain all nodes that we need to check for potential updates in their 
        /// children dependencies.
        /// </summary>
        private void OnSnapshotChanged(object sender, SnapshotChangedEventArgs e)
        {
            IDependenciesSnapshot snapshot = e.Snapshot;
            if (snapshot == null)
            {
                return;
            }

            lock (_snapshotChangeHandlerLock)
            {
                TrackChanges(e);
            }
        }

        /// <summary>
        /// Property ExpandedGraphContexts remembers graph expanded or checked so far.
        /// Each context represents one level in the graph, i.e. a node and its first level dependencies
        /// Tracking changes over all expanded contexts ensures that all levels are processed
        /// and updated when there are any changes in nodes data.
        /// </summary>
        private void TrackChanges(SnapshotChangedEventArgs updatedProjectContext)
        {
            IList<IGraphContext> expandedContexts;
            lock (_expandedGraphContextsLock)
            {
                expandedContexts = ExpandedGraphContexts.ToList();
            }

            if (expandedContexts.Count == 0)
            {
                return;
            }

            var actionHandlers = GraphActionHandlers.Select(x => x.Value).Where(x => x.CanHandleChanges()).ToList();

            if (actionHandlers.Count == 0)
            {
                return;
            }

            foreach (IGraphContext graphContext in expandedContexts)
            {
                try
                {
                    foreach (IDependenciesGraphActionHandler actionHandler in actionHandlers)
                    {
                        actionHandler.HandleChanges(graphContext, updatedProjectContext);
                    }
                }
                finally
                {
                    // Calling OnCompleted ensures that the changes are reflected in UI
                    graphContext.OnCompleted();
                }
            }
        }

        private void RegisterIcons(IEnumerable<ImageMoniker> icons)
        {
            Assumes.NotNull(icons);

            foreach (ImageMoniker icon in icons)
            {
                if (ThreadingTools.ApplyChangeOptimistically(ref _knownIcons, knownIcons => knownIcons.Add(icon)))
                {
                    _imageService.TryAssociateNameWithMoniker(GetIconStringName(icon), icon);
                }
            }
        }

        public GraphNode AddGraphNode(
            IGraphContext graphContext,
            string projectPath,
            GraphNode parentNode,
            IDependencyViewModel viewModel)
        {
            Assumes.True(IsInitialized);

            string modelId = viewModel.OriginalModel == null ? viewModel.Caption : viewModel.OriginalModel.Id;
            GraphNodeId newNodeId = GetGraphNodeId(projectPath, parentNode, modelId);
            return DoAddGraphNode(newNodeId, graphContext, projectPath, parentNode, viewModel);
        }

        public GraphNode AddTopLevelGraphNode(
            IGraphContext graphContext,
            string projectPath,
            IDependencyViewModel viewModel)
        {
            Assumes.True(IsInitialized);

            GraphNodeId newNodeId = GetTopLevelGraphNodeId(projectPath, viewModel.OriginalModel.GetTopLevelId());
            return DoAddGraphNode(newNodeId, graphContext, projectPath, parentNode: null, viewModel);
        }

        private GraphNode DoAddGraphNode(
            GraphNodeId graphNodeId,
            IGraphContext graphContext,
            string projectPath,
            GraphNode parentNode,
            IDependencyViewModel viewModel)
        {
            RegisterIcons(viewModel.GetIcons());

            GraphNode newNode = graphContext.Graph.Nodes.GetOrCreate(graphNodeId, viewModel.Caption, null);
            newNode.SetValue(DgmlNodeProperties.Icon, GetIconStringName(viewModel.Icon));
            // priority sets correct order among peers
            newNode.SetValue(CodeNodeProperties.SourceLocation,
                             new SourceLocation(projectPath, new Position(viewModel.Priority, 0)));
            newNode.AddCategory(DependenciesGraphSchema.CategoryDependency);

            if (viewModel.OriginalModel != null)
            {
                newNode.SetValue(DependenciesGraphSchema.DependencyIdProperty, viewModel.OriginalModel.Id);
                newNode.SetValue(DependenciesGraphSchema.ResolvedProperty, viewModel.OriginalModel.Resolved);
            }

            graphContext.OutputNodes.Add(newNode);

            if (parentNode != null)
            {
                graphContext.Graph.Links.GetOrCreate(parentNode, newNode, label: null, CodeLinkCategories.Contains);
            }

            return newNode;
        }

        public void RemoveGraphNode(IGraphContext graphContext,
                                     string projectPath,
                                     string modelId,
                                     GraphNode parentNode)
        {
            Assumes.True(IsInitialized);

            GraphNodeId id = GetGraphNodeId(projectPath, parentNode, modelId);
            GraphNode nodeToRemove = graphContext.Graph.Nodes.Get(id);

            if (nodeToRemove != null)
            {
                graphContext.OutputNodes.Remove(nodeToRemove);
                graphContext.Graph.Nodes.Remove(nodeToRemove);
            }
        }

        private static GraphNodeId GetGraphNodeId(string projectPath, GraphNode parentNode, string modelId)
        {
            var partialValues = new List<GraphNodeId>
            {
                GraphNodeId.GetPartial(CodeGraphNodeIdName.Assembly,
                                       new Uri(projectPath, UriKind.RelativeOrAbsolute)),
                GraphNodeId.GetPartial(CodeGraphNodeIdName.File,
                                       new Uri(modelId.ToLowerInvariant(), UriKind.RelativeOrAbsolute))
            };

            string parents;
            if (parentNode != null)
            {
                // to ensure Graph id for node is unique we add a hashcodes for node's parents separated by ';'
                parents = parentNode.Id.GetNestedValueByName<string>(CodeGraphNodeIdName.Namespace);
                if (string.IsNullOrEmpty(parents))
                {
                    string currentProject = parentNode.Id.GetValue(CodeGraphNodeIdName.Assembly) ?? projectPath;
                    parents = currentProject.GetHashCode().ToString();
                }
            }
            else
            {
                parents = projectPath.GetHashCode().ToString();
            }

            parents = parents + ";" + modelId.GetHashCode();
            partialValues.Add(GraphNodeId.GetPartial(CodeGraphNodeIdName.Namespace, parents));

            return GraphNodeId.GetNested(partialValues.ToArray());
        }

        private static GraphNodeId GetTopLevelGraphNodeId(string projectPath, string modelId)
        {
            var partialValues = new List<GraphNodeId>
            {
                GraphNodeId.GetPartial(CodeGraphNodeIdName.Assembly, new Uri(projectPath, UriKind.RelativeOrAbsolute))
            };

            string projectFolder = Path.GetDirectoryName(projectPath)?.ToLowerInvariant() ?? string.Empty;
            var filePath = new Uri(Path.Combine(projectFolder, modelId.ToLowerInvariant()), UriKind.RelativeOrAbsolute);

            partialValues.Add(GraphNodeId.GetPartial(CodeGraphNodeIdName.File, filePath));

            return GraphNodeId.GetNested(partialValues.ToArray());
        }

        private static string GetIconStringName(ImageMoniker icon)
        {
            return $"{icon.Guid.ToString()};{icon.Id}";
        }
    }
}
