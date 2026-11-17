using System.Threading.Tasks;
using Xunit;
using static CallFlowViewer.Tests.GraphAssert;

namespace CallFlowViewer.Tests
{
    /// <summary>"Show what this method calls": the flow going forward from the method.</summary>
    public class CalleesTests
    {
        [Fact]
        public async Task Calls_are_followed_into_implementations_and_library_calls_are_listed()
        {
            var solution = TestSolution.Single(@"
using System;
namespace App
{
    public interface IRepo { void Save(); }
    public class Repo : IRepo { public void Save() { Console.WriteLine(""saved""); } }
    public class Service
    {
        private readonly IRepo _repo = new Repo();
        public void Place() { Validate(); _repo.Save(); Console.WriteLine(""placed""); }
        private void Validate() { }
    }
}");
            var graph = await solution.CalleesAsync("App.Service", "Place");

            Assert.Equal("callees", graph.Direction);
            Assert.Equal(3, graph.Nodes.Count);
            Edge(graph, "Service.Place", "Service.Validate");
            Assert.Equal("interface IRepo", Edge(graph, "Service.Place", "Repo.Save").Via);
            Assert.Contains("Console.WriteLine", Node(graph, "Service.Place").External);
            Assert.Contains("Console.WriteLine", Node(graph, "Repo.Save").External);
        }

        [Fact]
        public async Task Delegates_and_raised_events_are_followed()
        {
            var solution = TestSolution.Single(@"
using System;
using System.Threading.Tasks;
namespace App
{
    public class Gateway
    {
        public event EventHandler Failed;
        public void Fire() { Failed?.Invoke(this, EventArgs.Empty); Task.Run(Cleanup); }
        private void Cleanup() { }
    }
    public class Sync
    {
        public Sync(Gateway gateway) { gateway.Failed += OnFailed; }
        private void OnFailed(object sender, EventArgs e) { }
    }
}");
            var graph = await solution.CalleesAsync("App.Gateway", "Fire");

            var raise = Edge(graph, "Gateway.Fire", "Sync.OnFailed");
            Assert.Equal("event", raise.Kind);
            Assert.Equal("Gateway.Failed", raise.Detail);

            var handOver = Edge(graph, "Gateway.Fire", "Gateway.Cleanup");
            Assert.Equal("delegate", handOver.Kind);
            Assert.Equal("Task.Run", handOver.Detail);
        }

        [Fact]
        public async Task Recursion_stops()
        {
            var solution = TestSolution.Single(@"
namespace App
{
    public class A
    {
        public void Ping(int n) { if (n > 0) Pong(n - 1); }
        public void Pong(int n) { if (n > 0) Ping(n - 1); }
    }
}");
            var graph = await solution.CalleesAsync("App.A", "Ping");

            Assert.Equal(2, graph.Nodes.Count);
            Edge(graph, "A.Ping", "A.Pong");
            Edge(graph, "A.Pong", "A.Ping");
        }
    }
}
