using System.Windows;
using System.Windows.Data;

namespace WpfStudio.Wpf.Diagnostics;

public static partial class BindingPathStateReader
{
    /// <summary>
    /// Captures opaque identities of cached WPF path state for stale-edit checks.
    /// Pending/inactive fields are compared, never interpreted as a current value.
    /// A null result is unsupported and must not authorize an edit.
    /// </summary>
    public static CachedIdentity? CaptureIdentity(BindingExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Target is not { } target || !target.CheckAccess()) return null;
        try
        {
            var shape = Metadata.Value;
            if (shape is null || !string.IsNullOrEmpty(expression.ParentBinding.XPath)) return null;
            uint flags = Convert.ToUInt32(shape.Flags.GetValue(expression));
            object? worker = shape.Worker.GetValue(expression);
            if (worker is null)
            {
                // Unused PriorityBinding candidates can legitimately have no worker.
                // This verifies absence, not a successfully evaluated path.
                return expression.Status is BindingStatus.Inactive or BindingStatus.Unattached or BindingStatus.Detached
                    ? new(flags, -1 - (int)expression.Status, [new(expression.ParentBinding)]) : null;
            }
            if (worker.GetType() != shape.WorkerType ||
                shape.PathWorker.GetValue(worker) is not { } pathWorker || pathWorker.GetType() != shape.PathWorkerType ||
                shape.Parent.GetValue(pathWorker) is not PropertyPath path ||
                shape.States.GetValue(pathWorker) is not Array states || shape.Infos.GetValue(path) is not Array infos ||
                states.Rank != 1 || infos.Rank != 1 || states.Length != infos.Length || states.Length is < 1 or > 64)
                return null;
            int status = Convert.ToInt32(shape.Status.GetValue(pathWorker));
            var identities = new List<WeakReference<object>?>();
            void Add(object? value) => identities.Add(value is null ? null : new(value));
            Add(pathWorker); Add(path); Add(states); Add(infos);
            int argumentCount = 0;
            for (int index = 0; index < states.Length; index++)
            {
                object state = states.GetValue(index)!;
                object info = infos.GetValue(index)!;
                object? owner = shape.StateItem.GetValue(state);
                if (owner is WeakReference weak)
                {
                    if (weak.GetType() != typeof(WeakReference)) return null;
                    owner = weak.Target;
                }
                Add(owner); Add(shape.StateInfo.GetValue(state)); Add(shape.StateType.GetValue(state));
                Add(shape.StateView.GetValue(state)); Add(shape.InfoName.GetValue(info)); Add(shape.InfoPropertyName.GetValue(info));
                object? rawArgs = shape.StateArgs.GetValue(state);
                if (rawArgs is not null and not object[]) return null;
                var args = rawArgs as object[];
                Add(args);
                if (args is not null)
                {
                    argumentCount += args.Length;
                    if (argumentCount > 256) return null;
                    foreach (object? argument in args) Add(argument);
                }
            }
            if (flags != Convert.ToUInt32(shape.Flags.GetValue(expression)) || status != Convert.ToInt32(shape.Status.GetValue(pathWorker)) ||
                !ReferenceEquals(states, shape.States.GetValue(pathWorker))) return null;
            return new(flags, status, identities.ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }

    /// <summary>Weak, non-serializable cached identities; no application metadata or value equality is invoked.</summary>
    public sealed class CachedIdentity
    {
        private readonly uint _flags;
        private readonly int _status;
        private readonly WeakReference<object>?[] _identities;
        internal CachedIdentity(uint flags, int status, WeakReference<object>?[] identities)
            => (_flags, _status, _identities) = (flags, status, identities);

        public bool Matches(CachedIdentity? other)
        {
            if (other is null || _flags != other._flags || _status != other._status || _identities.Length != other._identities.Length) return false;
            for (int index = 0; index < _identities.Length; index++)
            {
                var left = _identities[index];
                var right = other._identities[index];
                if (left is null || right is null) { if (left is not null || right is not null) return false; }
                else if (!left.TryGetTarget(out var first) || !right.TryGetTarget(out var second) || !ReferenceEquals(first, second)) return false;
            }
            return true;
        }
    }
}
