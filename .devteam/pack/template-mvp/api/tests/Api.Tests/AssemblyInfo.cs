// These tests start the whole app in memory with settings of their own. Starting several apps at the same moment lets one app's settings
// (such as the log folder or the sign-in mode) leak into another that is starting alongside it, so the tests run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
