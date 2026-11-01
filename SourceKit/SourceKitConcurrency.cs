using System;
using System.Threading;
using System.Threading.Tasks;

namespace SourceKit
{
    /// <summary>
    /// The thread ceiling and cancellation token every parallel loop in SourceKit reads.
    ///
    /// Process-wide rather than passed to each reader, for the same reason as the tools that use this
    /// library: it is configuration a person supplies once for a whole run, like an environment
    /// variable. A tool with its own setting forwards to this one, so a single `-threads` or Ctrl+C
    /// reaches the readers as well as the tool's own passes.
    /// </summary>
    public static class SourceKitConcurrency
    {
        private static int maxThreads = Environment.ProcessorCount;

        /// <summary>
        /// The thread ceiling for every parallel loop. Defaults to every core. Clamped to at least 1,
        /// since 0 or below is not a request that can be honoured.
        /// </summary>
        public static int MaxThreads
        {
            get => maxThreads;
            set => maxThreads = Math.Max(1, value);
        }

        /// <summary>The token every parallel loop polls. Defaults to <see cref="CancellationToken.None"/>.</summary>
        public static CancellationToken CancellationToken { get; set; } = CancellationToken.None;

        /// <summary>Fresh options reflecting the current settings, read at the start of each loop.</summary>
        public static ParallelOptions Options => new() { MaxDegreeOfParallelism = maxThreads, CancellationToken = CancellationToken };

        /// <summary>The check for the sequential seams between parallel loops.</summary>
        public static void ThrowIfCancelled() => CancellationToken.ThrowIfCancellationRequested();
    }
}
