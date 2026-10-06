using System.Globalization;
namespace DeyeSolar.Web.Components.Ui;
public static class UiFormat
{
 public static string Number(int? value,string format="0")=>value?.ToString(format,CultureInfo.CurrentCulture)??"—";
 public static string Number(double? value,string format="0.00")=>value is {} v&&double.IsFinite(v)?v.ToString(format,CultureInfo.CurrentCulture):"—";
 public static string Number(decimal? value,string format="0.00")=>value?.ToString(format,CultureInfo.CurrentCulture)??"—";
 public static string Signed(double? value,string format="0")=>value is {} v&&double.IsFinite(v)?(v<0?"−":"")+Math.Abs(v).ToString(format,CultureInfo.CurrentCulture):"—";
 public static string Time(DateTimeOffset value,string zone,string format="d MMM · HH:mm zzz")=>TimeZoneInfo.ConvertTime(value,TimeZoneInfo.FindSystemTimeZoneById(zone)).ToString(format,CultureInfo.CurrentCulture);
 public static string Duration(double? seconds)=>seconds is not {} value||!double.IsFinite(value)||value<0?"—":value>=3600?$"{(int)value/3600} h {(int)value%3600/60} min":$"{(int)value/60} min";
}
