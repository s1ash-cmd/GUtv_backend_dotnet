namespace GUtv_backend_dotnet.Models;

public enum UserRole
{
    User,
    Osnova,
    Ronin,
    Admin
}

public enum EqCategory
{
    Camera,
    Lens,
    Card,
    Battery,
    Charger,
    Sound,
    Stand,
    Light,
    Other
}

public enum EqAccess
{
    User,
    Osnova,
    Ronin
}

public enum BookingStatus
{
    Pending,
    Cancelled,
    Approved,
    Completed
}
