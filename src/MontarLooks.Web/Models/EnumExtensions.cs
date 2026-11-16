using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace MontarLooks.Web.Models;

public static class EnumExtensions
{
    public static string DisplayName(this Enum value) =>
        value.GetType().GetMember(value.ToString()).FirstOrDefault()
            ?.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? value.ToString();
}
