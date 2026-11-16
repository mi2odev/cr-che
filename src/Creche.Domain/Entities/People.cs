using Creche.Domain.Enums;

namespace Creche.Domain.Entities;

/// <summary>A household. Siblings share a family; invoices and payments are tracked per family.</summary>
public class Family
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Guardian> Guardians { get; set; } = [];
    public List<Child> Children { get; set; } = [];
}

/// <summary>A parent or legal guardian. Belongs to a family and so is shared by all siblings.</summary>
public class Guardian
{
    public int Id { get; set; }
    public int FamilyId { get; set; }
    public Family? Family { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public Relationship Relationship { get; set; }
    public string? Phone { get; set; }
    public string? Phone2 { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Job { get; set; }
    public bool IsPrimary { get; set; }
    public bool CanPickUp { get; set; } = true;
    public bool IsEmergencyContact { get; set; } = true;
    public bool IsArchived { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public class Child
{
    public int Id { get; set; }
    public int FamilyId { get; set; }
    public Family? Family { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }
    public Sex Sex { get; set; }
    public int? GroupId { get; set; }
    public Group? Group { get; set; }
    public ChildStatus Status { get; set; } = ChildStatus.Active;
    public DateOnly? EnrolmentDate { get; set; }
    public DateOnly? LeaveDate { get; set; }
    public string? LeaveReason { get; set; }
    public string? SchoolYear { get; set; }
    public int? FeePlanId { get; set; }
    public FeePlan? FeePlan { get; set; }
    /// <summary>Overrides the fee plan's monthly amount for this child when set.</summary>
    public decimal? CustomMonthlyFee { get; set; }
    public string? Allergies { get; set; }
    public string? MedicalNotes { get; set; }
    public string? SpecialInstructions { get; set; }
    /// <summary>File name inside the images folder.</summary>
    public string? PhotoFile { get; set; }
    public bool ActivityLogEnabled { get; set; } = true;
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Vaccination> Vaccinations { get; set; } = [];
    public List<ChildDocument> Documents { get; set; } = [];
    public List<AuthorizedPickup> AuthorizedPickups { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public class Vaccination
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly? Date { get; set; }
    public string? Notes { get; set; }
}

public class ChildDocument
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>File name inside the documents folder.</summary>
    public string FileName { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public bool IsArchived { get; set; }
}

/// <summary>Someone other than a guardian who may collect the child and/or be called in an emergency.</summary>
public class AuthorizedPickup
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string? RelationshipNote { get; set; }
    public string? IdNumber { get; set; }
    public bool CanPickUp { get; set; } = true;
    public bool IsEmergencyContact { get; set; }
    public bool IsArchived { get; set; }
}

public class Group
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int MinAgeMonths { get; set; }
    public int MaxAgeMonths { get; set; }
    public int Capacity { get; set; }
    public bool IsArchived { get; set; }

    public List<Child> Children { get; set; } = [];
    public List<Staff> Staff { get; set; } = [];
}

public class Staff
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public StaffRole Role { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public int? GroupId { get; set; }
    public Group? Group { get; set; }
    public decimal MonthlySalary { get; set; }
    public DateOnly? HireDate { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public class StaffAttendance
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public Staff? Staff { get; set; }
    public DateOnly Date { get; set; }
    public StaffAttendanceStatus Status { get; set; }
    public TimeOnly? CheckIn { get; set; }
    public TimeOnly? CheckOut { get; set; }
    public string? Notes { get; set; }
}

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int PasswordIterations { get; set; }
    public UserRole Role { get; set; }
    /// <summary>For educators: the only group they may work with.</summary>
    public int? GroupId { get; set; }
    public Group? Group { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
