using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static CallFlowViewer.Tests.GraphAssert;

namespace CallFlowViewer.Tests
{
    /// <summary>"Show call flow to this method": who calls the target, back to where the flow starts.</summary>
    public class CallersTests
    {
        [Fact]
        public async Task Direct_calls_are_followed_back_to_the_start()
        {
            var solution = TestSolution.Single(@"
namespace App
{
    public class A { public void Run() { new B().Do(); } }
    public class B { public void Do() { C.Save(); } }
    public static class C { public static void Save() { } }
}");
            var graph = await solution.CallersAsync("App.C", "Save");

            Assert.Equal(3, graph.Nodes.Count);
            Assert.Equal("call", Edge(graph, "A.Run", "B.Do").Kind);
            Assert.Equal("call", Edge(graph, "B.Do", "C.Save").Kind);
            Assert.NotNull(Node(graph, "A.Run").EntryKind);      // nothing calls it: a starting point
            Assert.Null(Node(graph, "B.Do").EntryKind);
            Assert.False(graph.Truncated);
        }

        [Fact]
        public async Task Calls_through_an_interface_are_followed_and_marked()
        {
            var solution = TestSolution.Single(@"
namespace App
{
    public interface IService { void Do(); }
    public class Service : IService { public void Do() { } }
    public class Controller
    {
        private readonly IService _service;
        public Controller(IService service) { _service = service; }
        public void Handle() { _service.Do(); }
    }
}");
            var graph = await solution.CallersAsync("App.Service", "Do");

            var edge = Edge(graph, "Controller.Handle", "Service.Do");
            Assert.Equal("interface IService", edge.Via);
        }

        [Fact]
        public async Task A_method_passed_as_a_delegate_is_found()
        {
            var solution = TestSolution.Single(@"
using System.Threading.Tasks;
namespace App
{
    public class Worker
    {
        public void Start() { Task.Run(Work); }
        public void Work() { }
    }
}");
            var graph = await solution.CallersAsync("App.Worker", "Work");

            var edge = Edge(graph, "Worker.Start", "Worker.Work");
            Assert.Equal("delegate", edge.Kind);
            Assert.Equal("Task.Run", edge.Detail);
        }

        [Fact]
        public async Task A_call_inside_a_lambda_is_marked()
        {
            var solution = TestSolution.Single(@"
using System.Threading.Tasks;
namespace App
{
    public class Worker
    {
        public void Start() { Task.Run(() => Work()); }
        public void Work() { }
    }
}");
            var graph = await solution.CallersAsync("App.Worker", "Work");

            var edge = Edge(graph, "Worker.Start", "Worker.Work");
            Assert.Equal("call", edge.Kind);
            Assert.True(edge.InsideLambda);
            Assert.Equal("Task.Run", edge.Sites[0].Detail);
        }

        [Fact]
        public async Task Nameof_and_cref_are_not_calls()
        {
            var solution = TestSolution.Single(@"
namespace App
{
    public class A
    {
        /// <summary>See <see cref=""Work""/>.</summary>
        public string Name() { return nameof(Work); }
        public void Work() { }
    }
}");
            var graph = await solution.CallersAsync("App.A", "Work");

            Assert.Empty(graph.Edges);
            Assert.Single(graph.Nodes);
        }

        [Fact]
        public async Task An_event_handler_is_followed_back_to_the_code_that_raises_the_event()
        {
            var solution = TestSolution.Single(@"
using System;
namespace App
{
    public class Gateway
    {
        public event EventHandler Failed;
        public void Fire() { Failed?.Invoke(this, EventArgs.Empty); }
    }
    public class Sync
    {
        private readonly Gateway _gateway;
        public Sync(Gateway gateway) { _gateway = gateway; }
        public void Start() { _gateway.Failed += OnFailed; }
        private void OnFailed(object sender, EventArgs e) { Store.Save(); }
    }
    public static class Store { public static void Save() { } }
}");
            var graph = await solution.CallersAsync("App.Store", "Save");

            Assert.Equal("call", Edge(graph, "Sync.OnFailed", "Store.Save").Kind);

            var raise = Edge(graph, "Gateway.Fire", "Sync.OnFailed");
            Assert.Equal("event", raise.Kind);
            Assert.Equal("Gateway.Failed", raise.Detail);

            // Subscribing is recorded, but it is not a call.
            var handler = Node(graph, "Sync.OnFailed");
            Assert.Single(handler.Subscriptions);
            Assert.Equal("Sync.Start()", handler.Subscriptions[0].By);
            Assert.False(graph.Edges.Any(e => graph.Nodes[e.From].Name.StartsWith("Sync.Start") && e.To == handler.Id));
        }

        [Fact]
        public async Task A_handler_of_an_event_outside_the_solution_becomes_a_starting_point()
        {
            var solution = TestSolution.Single(@"
using System;
using System.Timers;
namespace App
{
    public class Poller
    {
        private readonly Timer _timer = new Timer(1000);
        public void Start() { _timer.Elapsed += OnElapsed; }
        private void OnElapsed(object sender, ElapsedEventArgs e) { Store.Save(); }
    }
    public static class Store { public static void Save() { } }
}");
            var graph = await solution.CallersAsync("App.Store", "Save");

            var handler = Node(graph, "Poller.OnElapsed");
            Assert.StartsWith("Event handler", handler.EntryKind);
            Assert.Equal("Timer.Elapsed event", handler.EntryDetail);
        }

        [Fact]
        public async Task Recursion_does_not_loop_forever()
        {
            var solution = TestSolution.Single(@"
namespace App
{
    public class A
    {
        public void Loop(int n) { if (n > 0) Loop(n - 1); Target(); }
        public void Target() { }
    }
}");
            var graph = await solution.CallersAsync("App.A", "Target");

            Assert.Equal(2, graph.Nodes.Count);
            Edge(graph, "A.Loop", "A.Target");
        }

        [Fact]
        public async Task Http_endpoints_are_recognised_as_starting_points()
        {
            var solution = TestSolution.Single(@"
using System;
namespace App
{
    public class ApiControllerAttribute : Attribute { }
    public class RouteAttribute : Attribute { public RouteAttribute(string template) { } }
    public class HttpPostAttribute : Attribute { }
    public abstract class ControllerBase { }

    [ApiController, Route(""api/[controller]"")]
    public class OrdersController : ControllerBase
    {
        [HttpPost] public void Create() { Store.Save(); }
    }
    public static class Store { public static void Save() { } }
}");
            var graph = await solution.CallersAsync("App.Store", "Save");

            var start = Node(graph, "OrdersController.Create");
            Assert.Equal("HTTP endpoint", start.EntryKind);
            Assert.Equal("POST /api/orders", start.EntryDetail);
        }
    }
}
