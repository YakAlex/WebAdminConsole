using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

/// <summary>
/// MaintenanceWindow.Key ([JsonIgnore] in the Domain model) is computed,
/// with no setter, so it can't be a regular EF column. The PK here is the
/// shadow property "WindowKey", which MaintenanceRepository populates
/// with the value of window.Key before SaveChanges.
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
