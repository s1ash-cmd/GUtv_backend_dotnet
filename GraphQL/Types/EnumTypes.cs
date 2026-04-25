using GUtv_backend_dotnet.Models;
using HotChocolate.Types;

namespace GUtv_backend_dotnet.GraphQL.Types;

public sealed class UserRoleType : EnumType<UserRole>
{
    protected override void Configure(IEnumTypeDescriptor<UserRole> descriptor)
    {
        descriptor.Name("UserRole");
        descriptor.BindValuesExplicitly();
        descriptor.Value(UserRole.User).Name(nameof(UserRole.User));
        descriptor.Value(UserRole.Osnova).Name(nameof(UserRole.Osnova));
        descriptor.Value(UserRole.Ronin).Name(nameof(UserRole.Ronin));
        descriptor.Value(UserRole.Admin).Name(nameof(UserRole.Admin));
    }
}

public sealed class EqCategoryType : EnumType<EqCategory>
{
    protected override void Configure(IEnumTypeDescriptor<EqCategory> descriptor)
    {
        descriptor.Name("EqCategory");
        descriptor.BindValuesExplicitly();
        descriptor.Value(EqCategory.Camera).Name(nameof(EqCategory.Camera));
        descriptor.Value(EqCategory.Lens).Name(nameof(EqCategory.Lens));
        descriptor.Value(EqCategory.Card).Name(nameof(EqCategory.Card));
        descriptor.Value(EqCategory.Battery).Name(nameof(EqCategory.Battery));
        descriptor.Value(EqCategory.Charger).Name(nameof(EqCategory.Charger));
        descriptor.Value(EqCategory.Sound).Name(nameof(EqCategory.Sound));
        descriptor.Value(EqCategory.Stand).Name(nameof(EqCategory.Stand));
        descriptor.Value(EqCategory.Light).Name(nameof(EqCategory.Light));
        descriptor.Value(EqCategory.Other).Name(nameof(EqCategory.Other));
    }
}

public sealed class EqAccessType : EnumType<EqAccess>
{
    protected override void Configure(IEnumTypeDescriptor<EqAccess> descriptor)
    {
        descriptor.Name("EqAccess");
        descriptor.BindValuesExplicitly();
        descriptor.Value(EqAccess.User).Name(nameof(EqAccess.User));
        descriptor.Value(EqAccess.Osnova).Name(nameof(EqAccess.Osnova));
        descriptor.Value(EqAccess.Ronin).Name(nameof(EqAccess.Ronin));
    }
}

public sealed class BookingStatusType : EnumType<BookingStatus>
{
    protected override void Configure(IEnumTypeDescriptor<BookingStatus> descriptor)
    {
        descriptor.Name("BookingStatus");
        descriptor.BindValuesExplicitly();
        descriptor.Value(BookingStatus.Pending).Name(nameof(BookingStatus.Pending));
        descriptor.Value(BookingStatus.Cancelled).Name(nameof(BookingStatus.Cancelled));
        descriptor.Value(BookingStatus.Approved).Name(nameof(BookingStatus.Approved));
        descriptor.Value(BookingStatus.Completed).Name(nameof(BookingStatus.Completed));
    }
}
