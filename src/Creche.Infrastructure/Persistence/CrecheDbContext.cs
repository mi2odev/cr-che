using Creche.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Creche.Infrastructure.Persistence;

public class CrecheDbContext(DbContextOptions<CrecheDbContext> options) : DbContext(options)
{
    public DbSet<Family> Families => Set<Family>();
    public DbSet<Guardian> Guardians => Set<Guardian>();
    public DbSet<Child> Children => Set<Child>();
    public DbSet<Vaccination> Vaccinations => Set<Vaccination>();
    public DbSet<ChildDocument> ChildDocuments => Set<ChildDocument>();
    public DbSet<AuthorizedPickup> AuthorizedPickups => Set<AuthorizedPickup>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<StaffAttendance> StaffAttendance => Set<StaffAttendance>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AttendanceRecord> Attendance => Set<AttendanceRecord>();
    public DbSet<ActivityLogEntry> ActivityLog => Set<ActivityLogEntry>();
    public DbSet<FeePlan> FeePlans => Set<FeePlan>();
    public DbSet<ExtraCharge> ExtraCharges => Set<ExtraCharge>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Family>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.HasIndex(x => x.Name);
        });

        b.Entity<Guardian>(e =>
        {
            e.Property(x => x.FirstName).HasMaxLength(80).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(80).IsRequired();
            e.Ignore(x => x.FullName);
            e.HasOne(x => x.Family).WithMany(f => f.Guardians).HasForeignKey(x => x.FamilyId);
        });

        b.Entity<Child>(e =>
        {
            e.Property(x => x.FirstName).HasMaxLength(80).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(80).IsRequired();
            e.Ignore(x => x.FullName);
            e.HasOne(x => x.Family).WithMany(f => f.Children).HasForeignKey(x => x.FamilyId);
            e.HasOne(x => x.Group).WithMany(g => g.Children).HasForeignKey(x => x.GroupId);
            e.HasOne(x => x.FeePlan).WithMany().HasForeignKey(x => x.FeePlanId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.LastName, x.FirstName });
        });

        b.Entity<Vaccination>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.HasOne<Child>().WithMany(c => c.Vaccinations).HasForeignKey(x => x.ChildId);
        });
        b.Entity<ChildDocument>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(160).IsRequired();
            e.HasOne<Child>().WithMany(c => c.Documents).HasForeignKey(x => x.ChildId);
        });
        b.Entity<AuthorizedPickup>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(160).IsRequired();
            e.HasOne<Child>().WithMany(c => c.AuthorizedPickups).HasForeignKey(x => x.ChildId);
        });

        b.Entity<Group>(e => e.Property(x => x.Name).HasMaxLength(80).IsRequired());

        b.Entity<Staff>(e =>
        {
            e.Property(x => x.FirstName).HasMaxLength(80).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(80).IsRequired();
            e.Ignore(x => x.FullName);
            e.HasOne(x => x.Group).WithMany(g => g.Staff).HasForeignKey(x => x.GroupId);
        });
        b.Entity<StaffAttendance>(e =>
        {
            e.HasOne(x => x.Staff).WithMany().HasForeignKey(x => x.StaffId);
            e.HasIndex(x => new { x.StaffId, x.Date }).IsUnique();
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(60).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId);
        });

        b.Entity<AttendanceRecord>(e =>
        {
            e.HasOne(x => x.Child).WithMany().HasForeignKey(x => x.ChildId);
            // One live record per child and day; records flagged as deleted are kept for history.
            e.HasIndex(x => new { x.ChildId, x.Date }).IsUnique().HasFilter("\"IsDeleted\" = 0");
            e.HasIndex(x => x.Date);
        });
        b.Entity<ActivityLogEntry>(e =>
        {
            e.HasOne(x => x.Child).WithMany().HasForeignKey(x => x.ChildId);
            e.HasIndex(x => new { x.ChildId, x.Date });
        });

        b.Entity<FeePlan>(e => e.Property(x => x.Name).HasMaxLength(80).IsRequired());

        b.Entity<ExtraCharge>(e =>
        {
            e.HasOne(x => x.Child).WithMany().HasForeignKey(x => x.ChildId);
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId);
        });

        b.Entity<Invoice>(e =>
        {
            e.Ignore(x => x.Remaining);
            e.Property(x => x.Number).HasMaxLength(30);
            e.HasIndex(x => x.Number);
            e.HasIndex(x => new { x.ChildId, x.Period });
            e.HasIndex(x => new { x.FamilyId, x.Status });
            e.HasOne(x => x.Family).WithMany().HasForeignKey(x => x.FamilyId);
            e.HasOne(x => x.Child).WithMany().HasForeignKey(x => x.ChildId);
        });
        b.Entity<InvoiceLine>(e => e.HasOne<Invoice>().WithMany(i => i.Lines).HasForeignKey(x => x.InvoiceId));

        b.Entity<Payment>(e =>
        {
            e.Property(x => x.ReceiptNumber).HasMaxLength(30);
            e.HasIndex(x => x.ReceiptNumber);
            e.HasIndex(x => x.Date);
            e.HasOne(x => x.Family).WithMany().HasForeignKey(x => x.FamilyId);
        });
        b.Entity<PaymentAllocation>(e =>
        {
            e.HasOne(x => x.Payment).WithMany(p => p.Allocations).HasForeignKey(x => x.PaymentId);
            e.HasOne(x => x.Invoice).WithMany(i => i.Allocations).HasForeignKey(x => x.InvoiceId);
        });

        b.Entity<Expense>(e =>
        {
            e.HasOne(x => x.Staff).WithMany().HasForeignKey(x => x.StaffId);
            e.HasIndex(x => x.Date);
        });

        b.Entity<SettingEntry>(e => e.HasKey(x => x.Key));

        // History is never removed behind the user's back: no cascading deletes anywhere.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}
