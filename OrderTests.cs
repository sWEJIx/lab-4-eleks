using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class OrderServiceAndValidatorTests
{
    private static AppDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task CreateOrderAsync_Applies10PercentDiscount_WhenQuantityIs5OrMore()
    {
        using var db = GetInMemoryDbContext();
        var service = new OrderService(db);
        var dto = new CreateOrderDto("Gaming Laptop", 5, 1000m);

        var result = await service.CreateOrderAsync(dto, "user-123");

        Assert.Equal(4500m, result.TotalPrice);
    }

    [Fact]
    public async Task CreateOrderAsync_DoesNotApplyDiscount_WhenQuantityIsLessThan5()
    {
        using var db = GetInMemoryDbContext();
        var service = new OrderService(db);
        var dto = new CreateOrderDto("Mouse", 2, 500m);

        var result = await service.CreateOrderAsync(dto, "user-123");

        Assert.Equal(1000m, result.TotalPrice);
    }

    [Fact]
    public async Task CreateOrderAsync_ThrowsBusinessRuleException_WhenQuantityIsZeroOrLess()
    {
        using var db = GetInMemoryDbContext();
        var service = new OrderService(db);
        var dto = new CreateOrderDto("Keyboard", 0, 500m);

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateOrderAsync(dto, "user-123"));
    }

    [Fact]
    public async Task GetOrderByIdAsync_ThrowsCustomNotFoundException_WhenOrderDoesNotExist()
    {
        using var db = GetInMemoryDbContext();
        var service = new OrderService(db);

        await Assert.ThrowsAsync<CustomNotFoundException>(() => service.GetOrderByIdAsync(999, "user-123"));
    }

    [Fact]
    public void Validator_ReturnsErrors_WhenDtoIsInvalid()
    {
        var validator = new CreateOrderDtoValidator();
        var invalidDto = new CreateOrderDto("AB", 0, -50m);

        var result = validator.Validate(invalidDto);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
    }
}