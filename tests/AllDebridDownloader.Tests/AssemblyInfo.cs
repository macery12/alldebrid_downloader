// WPF tests create an Application on their own STA thread. xunit runs test classes in
// parallel by default, so those threads can be alive -- but not pumping a message loop --
// while other tests run. Anything the app marshals to Application.Current.Dispatcher would
// then be posted into a queue nobody drains, and the test would see stale state.
//
// The suite runs in about four seconds, so serialising it is cheap and makes it
// deterministic.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
