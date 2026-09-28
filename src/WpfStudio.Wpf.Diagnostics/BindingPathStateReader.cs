using System.Reflection;
using System.Windows;
using System.Windows.Data;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

/// <summary>
/// Reads only WPF's already-cached path fields. This is a version/shape-guarded
/// implementation adapter, not a supported WPF API or a fresh source evaluation.
/// No PropertyPathWorker methods, application accessors, or converters are invoked.
/// </summary>
public static partial class BindingPathStateReader
{
    private static readonly Lazy<Shape?> Metadata = new(Shape.TryCreate);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static InspectionBindingPathState Capture(BindingExpression expression, int maximumSegments = 64)
    {
        ArgumentNullException.ThrowIfNull(expression);
        string? targetType = expression.TargetProperty is { } targetProperty ? BindingDiagnosticText.TypeName(targetProperty.PropertyType) : null;
        InspectionBindingPathState Unavailable(string reason) => new(false, "Unavailable", [], UnavailableReason: reason, TargetType: targetType);
        if (expression.Target is not { } target || !target.CheckAccess())
            return Unavailable("Cached binding state is available only on its attached target's dispatcher.");
        try
        {
            var shape = Metadata.Value;
            if (shape is null) return Unavailable("This WPF version or private path-state shape is unsupported; no source path was evaluated.");
            if (shape.Worker.GetValue(expression) is not { } worker || worker.GetType() != shape.WorkerType ||
                shape.PathWorker.GetValue(worker) is not { } pathWorker || pathWorker.GetType() != shape.PathWorkerType)
                return Unavailable("WPF has no supported cached CLR path worker for this expression.");
            if (!string.IsNullOrEmpty(expression.ParentBinding.XPath))
                return Unavailable("XML/XPath path-state interpretation is not supported by this cached CLR adapter.");
            if (shape.Parent.GetValue(pathWorker) is not PropertyPath path ||
                shape.States.GetValue(pathWorker) is not Array states || shape.Infos.GetValue(path) is not Array infos ||
                states.Rank != 1 || infos.Rank != 1 || states.Length != infos.Length || states.Length == 0)
                return Unavailable("The cached path description and value-state arrays do not have a supported matching shape.");

            uint flags = Convert.ToUInt32(shape.Flags.GetValue(expression));
            var publicStatus = expression.Status;
            int status = Convert.ToInt32(shape.Status.GetValue(pathWorker));
            bool fallback = (flags & shape.Fallback) != 0;
            if ((flags & shape.InProgress) != 0 || publicStatus is BindingStatus.AsyncRequestPending or BindingStatus.Detached or BindingStatus.Unattached or BindingStatus.Inactive ||
                status != shape.Active && status != shape.PathError)
                return new(true, "NotObserved", [], UnavailableReason:
                    "WPF is pending, inactive, attaching, detaching, or updating. Cached downstream segments may belong to an earlier evaluation and were not interpreted.",
                    TargetType: targetType, UsesFallbackValue: fallback);

            int count = Math.Min(states.Length, Math.Clamp(maximumSegments, 0, 64));
            var rows = new List<InspectionBindingPathSegment>(count);
            int? unresolved = null;
            bool blocked = false;
            for (int level = 0; level < count; level++)
            {
                object state = states.GetValue(level)!;
                object info = infos.GetValue(level)!;
                int kindValue = Convert.ToInt32(shape.InfoKind.GetValue(info));
                string kind = kindValue == shape.Property ? "Property" : kindValue == shape.Indexer ? "Indexer" : kindValue == shape.Direct ? "Direct" : "Unsupported";
                string? name = shape.InfoPropertyName.GetValue(info) as string ?? shape.InfoName.GetValue(info) as string;
                if (name is not null) name = BindingDiagnosticText.Limit(name, 256);
                if (kind == "Indexer" && name is null) name = "[indexer]";
                if (blocked)
                {
                    rows.Add(new(level, kind, name, "NotObserved"));
                    continue;
                }
                object? accessor = shape.StateInfo.GetValue(state);
                object? reference = shape.StateItem.GetValue(state);
                bool view = shape.StateView.GetValue(state) is not null;
                object? owner;
                if (reference is WeakReference weak)
                {
                    // WPF creates exact System.WeakReference instances. Never invoke
                    // a user override of WeakReference.Target on a foreign shape.
                    if (weak.GetType() != typeof(WeakReference)) return Unavailable("A cached source reference has an unsupported implementation.");
                    owner = weak.Target;
                }
                else owner = reference;
                bool sentinel = ReferenceEquals(owner, shape.NullItem) || ReferenceEquals(owner, shape.Disconnected) ||
                    ReferenceEquals(owner, DependencyProperty.UnsetValue);
                string stateName;
                string? ownerType = owner is null || sentinel || ReferenceEquals(owner, shape.StaticSource) ? null : BindingDiagnosticText.TypeName(owner.GetType());
                string? accessorKind = null;
                string? valueType = null;
                if (accessor is null)
                {
                    stateName = status == shape.PathError && kind is "Property" or "Indexer" ? "Unresolved" : "NotObserved";
                    if (stateName == "Unresolved") unresolved = level;
                    blocked = true;
                }
                else if (sentinel || owner is null && kind != "Direct")
                {
                    stateName = view && ReferenceEquals(owner, shape.NullItem) ? "NoCurrentItem" : "Unavailable";
                    blocked = true;
                }
                else
                {
                    accessorKind = AccessorKind(accessor);
                    // A descriptor/custom PropertyInfo is evidence that WPF already
                    // resolved an accessor. Its own metadata is never queried here;
                    // names and value types come only from WPF's cached fields.
                    stateName = kind == "Direct" ? "Direct" : accessorKind is "DependencyProperty" or "RuntimeProperty"
                        or "PropertyDescriptor (metadata not invoked)" or "Custom PropertyInfo (metadata not invoked)" ? "Resolved" : "UnsupportedAccessor";
                    if (shape.StateType.GetValue(state) is Type cachedType) valueType = BindingDiagnosticText.TypeName(cachedType);
                    if (kind == "Unsupported") { stateName = "NotObserved"; blocked = true; }
                }
                rows.Add(new(level, kind, name, stateName, ownerType, accessorKind, valueType, view));
            }
            if (publicStatus != expression.Status || flags != Convert.ToUInt32(shape.Flags.GetValue(expression)) ||
                status != Convert.ToInt32(shape.Status.GetValue(pathWorker)) || !ReferenceEquals(states, shape.States.GetValue(pathWorker)))
                return Unavailable("WPF's cached binding state changed while it was observed.");
            return new(true, status == shape.PathError ? "PathError" : "Active", rows.ToArray(), unresolved,
                states.Length > count, unresolved is null ? null :
                    "This is the first unresolved cached segment. WPF may erase the failed segment's owner, so these fields alone do not distinguish a missing member from a null intermediate value.",
                targetType, fallback);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("WPF's cached path fields could not be read safely; no source path was evaluated."); }
    }

    private static string AccessorKind(object accessor)
    {
        if (accessor is DependencyProperty) return "DependencyProperty";
        Type type = accessor.GetType();
        if (accessor is PropertyInfo && type.Assembly == typeof(object).Assembly && type.FullName == "System.Reflection.RuntimePropertyInfo")
            return "RuntimeProperty";
        if (accessor is System.ComponentModel.PropertyDescriptor) return "PropertyDescriptor (metadata not invoked)";
        if (accessor is PropertyInfo) return "Custom PropertyInfo (metadata not invoked)";
        return ReferenceEquals(accessor, DependencyProperty.UnsetValue) ? "Direct" : "Unsupported";
    }

    private sealed class Shape
    {
        internal required Type WorkerType, PathWorkerType;
        internal required FieldInfo Worker, PathWorker, Parent, Status, States, Infos, Flags;
        internal required FieldInfo StateItem, StateInfo, StateType, StateView, StateArgs, InfoKind, InfoName, InfoPropertyName;
        internal required object NullItem, StaticSource, Disconnected;
        internal required int Active, PathError, Property, Indexer, Direct;
        internal required uint InProgress, Fallback;

        internal static Shape? TryCreate()
        {
            try
            {
                var assembly = typeof(BindingExpression).Assembly;
                if (assembly.GetName().Version?.Major is not (>= 8 and <= 10)) return null;
                Type worker = assembly.GetType("MS.Internal.Data.ClrBindingWorker", true)!;
                Type pathWorker = assembly.GetType("MS.Internal.Data.PropertyPathWorker", true)!;
                var workerField = Require(typeof(BindingExpression), "_worker", assembly.GetType("MS.Internal.Data.BindingWorker", true)!);
                var states = Require(pathWorker, "_arySVS");
                var infos = Require(typeof(PropertyPath), "_arySVI");
                var state = states.FieldType.GetElementType();
                var info = infos.FieldType.GetElementType();
                if (!states.FieldType.IsArray || !infos.FieldType.IsArray || state?.FullName != "MS.Internal.Data.PropertyPathWorker+SourceValueState" ||
                    info?.FullName != "MS.Internal.Data.SourceValueInfo" || !state.IsValueType || !info.IsValueType || state.Assembly != assembly || info.Assembly != assembly)
                    return null;
                var status = Require(pathWorker, "_status");
                var kind = Require(info, "type");
                var flags = Require(typeof(BindingExpressionBase), "_flags");
                if (status.FieldType.FullName != "System.Windows.PropertyPathStatus" || kind.FieldType.FullName != "MS.Internal.Data.SourceValueType" ||
                    flags.FieldType.FullName != "System.Windows.Data.BindingExpressionBase+PrivateFlags" ||
                    !status.FieldType.IsEnum || !kind.FieldType.IsEnum || !flags.FieldType.IsEnum ||
                    Enum.GetUnderlyingType(status.FieldType) != typeof(byte) || Enum.GetUnderlyingType(kind.FieldType) != typeof(int) ||
                    Enum.GetUnderlyingType(flags.FieldType) != typeof(uint)) return null;
                return new()
                {
                    WorkerType = worker, PathWorkerType = pathWorker, Worker = workerField,
                    PathWorker = Require(worker, "_pathWorker", pathWorker), Parent = Require(pathWorker, "_parent", typeof(PropertyPath)),
                    Status = status, States = states, Infos = infos, Flags = flags,
                    StateItem = Require(state, "item", typeof(object)), StateInfo = Require(state, "info", typeof(object)),
                    StateType = Require(state, "type", typeof(Type)), StateView = Require(state, "collectionView", typeof(System.ComponentModel.ICollectionView)),
                    StateArgs = Require(state, "args", typeof(object[])),
                    InfoKind = kind, InfoName = Require(info, "name", typeof(string)), InfoPropertyName = Require(info, "propertyName", typeof(string)),
                    NullItem = Static(typeof(BindingExpression), "NullDataItem"), StaticSource = Static(typeof(BindingExpression), "StaticSource"),
                    Disconnected = Static(typeof(BindingExpressionBase), "DisconnectedItem"),
                    Active = Number(status.FieldType, "Active"), PathError = Number(status.FieldType, "PathError"),
                    Property = Number(kind.FieldType, "Property"), Indexer = Number(kind.FieldType, "Indexer"), Direct = Number(kind.FieldType, "Direct"),
                    InProgress = Bits(flags.FieldType, "iInTransfer", "iInUpdate", "iAttaching", "iDetaching",
                        "iTransferPending", "iNeedDataTransfer", "iTransferDeferred"),
                    Fallback = Bits(flags.FieldType, "iUsingFallbackValue")
                };
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
        }

        private static FieldInfo Require(Type owner, string name, Type? expected = null)
        {
            var field = owner.GetField(name, Fields) ?? throw new MissingFieldException();
            if (expected is not null && field.FieldType != expected) throw new MissingFieldException();
            return field;
        }
        private static object Static(Type owner, string name) => Require(owner, name, typeof(object)).GetValue(null) ?? throw new MissingFieldException();
        private static int Number(Type type, string name) => Convert.ToInt32(Require(type, name).GetRawConstantValue());
        private static uint Bits(Type type, params string[] names) => names.Aggregate(0u, (bits, name) => bits | Convert.ToUInt32(Require(type, name).GetRawConstantValue()));
    }
}
