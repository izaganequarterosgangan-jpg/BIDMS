using Xunit;

// CertificateTemplateRenderer keeps the "baked-in literal" identities in private
// static fields configured at startup. Two test classes touching them
// concurrently would race and produce flaky, order-dependent failures, so the
// assembly runs its collections one at a time. The suite is small enough that
// serialising it costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]