namespace Creche.Domain.Enums;

public enum Sex { Male = 0, Female = 1 }

public enum ChildStatus { Active = 0, WaitingList = 1, Left = 2 }

public enum Relationship { Mother = 0, Father = 1, Guardian = 2, Grandparent = 3, Sibling = 4, Other = 5 }

public enum AttendanceStatus { Present = 0, Absent = 1 }

public enum FeePlanType { FullDay = 0, HalfDay = 1, Hourly = 2 }

public enum InvoiceStatus { Unpaid = 0, PartiallyPaid = 1, Paid = 2, Cancelled = 3 }

public enum ChargeKind
{
    MonthlyFee = 0,
    RegistrationFee = 1,
    Meals = 2,
    LatePickup = 3,
    Activity = 4,
    Transport = 5,
    SiblingDiscount = 6,
    Other = 7,
}

public enum PaymentMethod { Cash = 0, Card = 1, Other = 2 }

public enum StaffRole { Director = 0, Educator = 1, Assistant = 2, Cook = 3, Cleaner = 4, Driver = 5, Administration = 6, Other = 7 }

public enum StaffAttendanceStatus { Present = 0, Absent = 1, Leave = 2 }

public enum ExpenseCategory { Rent = 0, Salaries = 1, Food = 2, Supplies = 3, Repairs = 4, Utilities = 5, Other = 6 }

public enum UserRole { Admin = 0, Secretary = 1, Educator = 2 }

public enum ActivityType { Meal = 0, Nap = 1, Incident = 2, Note = 3 }

/// <summary>What a signed-in user is allowed to do. Checked in the service layer.</summary>
public enum Permission
{
    Dashboard,
    Children,
    Families,
    GroupsView,
    GroupsManage,
    Enrolment,
    Attendance,
    ActivityLog,
    Billing,
    FeePlansManage,
    Payments,
    Staff,
    Expenses,
    Reports,
    Users,
    Settings,
    Backup,
    Search,
}
