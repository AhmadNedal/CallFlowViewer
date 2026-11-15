// Try it: put the caret on "SaveAsync" in OrderRepository below, press Ctrl+.
// and choose "Show call flow to this method".

var repo = new OrderRepository();
IOrderService service = new OrderService(repo);
var controller = new OrdersController(service);

await controller.Create("Ahmad");
await new ExpiredOrdersWorker(service).RunOnceAsync();

public record Order(Guid Id, string Customer, bool Cancelled = false);

public interface IOrderRepository
{
    Task SaveAsync(Order order);
}

public class OrderRepository : IOrderRepository
{
    public Task SaveAsync(Order order)
    {
        Console.WriteLine($"Saved {order.Id} for {order.Customer}");
        return Task.CompletedTask;
    }
}

public interface IOrderService
{
    Task<Guid> PlaceOrderAsync(string customer);
    Task CancelAsync(Guid id);
}

public class OrderService : IOrderService
{
    private readonly IOrderRepository _orders;
    public OrderService(IOrderRepository orders) => _orders = orders;

    public async Task<Guid> PlaceOrderAsync(string customer)
    {
        var order = new Order(Guid.NewGuid(), customer);
        await _orders.SaveAsync(order);
        return order.Id;
    }

    public async Task CancelAsync(Guid id)
    {
        await _orders.SaveAsync(new Order(id, "?", Cancelled: true));
    }
}

public class OrdersController
{
    private readonly IOrderService _orders;
    public OrdersController(IOrderService orders) => _orders = orders;

    public Task<Guid> Create(string customer) => _orders.PlaceOrderAsync(customer);

    public Task Cancel(Guid id) => _orders.CancelAsync(id);
}

public class ExpiredOrdersWorker
{
    private readonly IOrderService _service;
    public ExpiredOrdersWorker(IOrderService service) => _service = service;

    public async Task RunOnceAsync()
    {
        var expired = new List<Guid> { Guid.NewGuid() };
        foreach (var id in expired)
            await Task.Run(() => _service.CancelAsync(id));
    }
}
