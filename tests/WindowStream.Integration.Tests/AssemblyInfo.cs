// Disable test parallelism for the whole integration test assembly.
// Integration tests that spawn desktop processes (Notepad, WGC sessions, NVENC) and
// use GPU/system resources must not run in parallel — doing so causes race conditions
// where one test's Notepad cleanup kills another test's window, and GPU-exclusive
// resources (WGC D3D11 device, NVENC session) conflict.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
