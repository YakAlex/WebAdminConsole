using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

/// <summary>
/// MaintenanceWindow.Key ([JsonIgnore] у Domain-моделі) — обчислюваний,
/// без сеттера, тому не може бути звичайним EF-стовпцем. PK тут —
/// тіньова властивість "WindowKey", яку MaintenanceRepository заповнює
/// значенням window.Key перед SaveChanges.
/// </summary>
public sealed class MaintenanceWindowConfiguration : IEntityTypeConfiguration<MaintenanceWindow>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindow> builder)
    {
        builder.ToTable("MaintenanceWindows");

        builder.Property<string>("WindowKey");
        builder.HasKey("WindowKey");

        builder.Property(w => w.DisplayName).IsRequired();
        builder.Property(w => w.Reason).IsRequired();

        builder.Ignore(w => w.Key);
    }
}
