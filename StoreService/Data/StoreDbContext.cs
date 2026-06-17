using Microsoft.EntityFrameworkCore;
using StoreService.Models;

namespace StoreService.Data;

public class StoreDbContext : DbContext
{
    public StoreDbContext(DbContextOptions<StoreDbContext> options) : base(options) { }

    public DbSet<Item> Items => Set<Item>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Заполняем магазин начальными товарами (10 штук)
        modelBuilder.Entity<Item>().HasData(
            new Item { Id = 1, Name = "Улучшенный курсор", Description = "+1 балл за каждый клик", Price = 100 },
            new Item { Id = 2, Name = "Игровая мышь", Description = "+3 балла за каждый клик", Price = 300 },
            new Item { Id = 3, Name = "Бабушкин компьютер", Description = "+5 баллов за каждый клик", Price = 500 },
            new Item { Id = 4, Name = "Видеокарта начального уровня", Description = "+10 баллов за каждый клик", Price = 1000 },
            new Item { Id = 5, Name = "Топовая видеокарта", Description = "+25 баллов за каждый клик", Price = 2500 },
            new Item { Id = 6, Name = "Майнинг ферма", Description = "+50 баллов за каждый клик", Price = 5000 },
            new Item { Id = 7, Name = "Серверная стойка", Description = "+100 баллов за каждый клик", Price = 10000 },
            new Item { Id = 8, Name = "Суперкомпьютер", Description = "+250 баллов за каждый клик", Price = 25000 },
            new Item { Id = 9, Name = "Квантовый компьютер", Description = "+500 баллов за каждый клик", Price = 50000 },
            new Item { Id = 10, Name = "Собственный дата-центр", Description = "+1000 баллов за каждый клик", Price = 100000 }
        );
    }
}