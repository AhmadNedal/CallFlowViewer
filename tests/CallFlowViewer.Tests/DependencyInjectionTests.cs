using System.Threading.Tasks;
using Xunit;
using static CallFlowViewer.Tests.GraphAssert;

namespace CallFlowViewer.Tests
{
    public class DependencyInjectionTests
    {
        private const string Code = @"
using Microsoft.Extensions.DependencyInjection;
namespace App
{
    public interface IService { void Do(); }
    public class Service : IService { public void Do() { } }
    public class Other : IService { public void Do() { } }
    public class Controller
    {
        private readonly IService _service;
        public Controller(IService service) { _service = service; }
        public void Handle() { _service.Do(); }
    }
    public static class Startup
    {
        public static void Configure(IServiceCollection services) { services.AddScoped<IService, Other>(); }
    }
}";

        private static TestSolution Create()
        {
            var solution = new TestSolution();
            solution.AddProject("App", new[] { Code }, extra: new[] { TestSolution.DependencyInjection });
            return solution;
        }

        [Fact]
        public async Task A_call_to_the_registered_class_is_marked_registered()
        {
            var graph = await Create().CallersAsync("App.Other", "Do");

            var edge = Edge(graph, "Controller.Handle", "Other.Do");
            Assert.NotNull(edge.Di);
            Assert.Equal("registered", edge.Di.Status);
            Assert.Equal("Scoped", edge.Di.Registrations[0].Lifetime);
        }

        [Fact]
        public async Task A_call_to_a_class_that_is_not_registered_is_flagged()
        {
            var graph = await Create().CallersAsync("App.Service", "Do");

            var edge = Edge(graph, "Controller.Handle", "Service.Do");
            Assert.NotNull(edge.Di);
            Assert.Equal("other", edge.Di.Status);
        }

        [Fact]
        public async Task Without_registrations_the_check_says_nothing()
        {
            var solution = TestSolution.Single(@"
namespace App
{
    public interface IService { void Do(); }
    public class Service : IService { public void Do() { } }
    public class Controller { private readonly IService _s; public Controller(IService s) { _s = s; } public void Handle() { _s.Do(); } }
}");
            var graph = await solution.CallersAsync("App.Service", "Do");

            Assert.Null(Edge(graph, "Controller.Handle", "Service.Do").Di);
        }
    }
}
