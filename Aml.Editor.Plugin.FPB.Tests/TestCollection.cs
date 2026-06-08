// Aml.Engine maintains a process-wide static cache that races when multiple
// CAEXDocument instances are built in parallel — the symptom is intermittent
// "Roundtrip_PreservesElementCounts" failures with mismatched element counts.
// The mapper test project applies the same fix.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
