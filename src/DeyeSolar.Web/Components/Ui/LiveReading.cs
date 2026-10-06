using DeyeSolar.Domain.Models;
namespace DeyeSolar.Web.Components.Ui;
public sealed record LiveReading(double? SolarKw,double? LoadKw,double? GridKw,double? BatteryKw,int? BatterySoc,DateTimeOffset? SolarAt,DateTimeOffset? GridAt,DateTimeOffset? PolledAt)
{
 public static LiveReading Read(InverterData? data,DateTimeOffset now,string? expectedDevice,int maximumAgeMinutes=10)
 {
  bool Fresh(DateTimeOffset? at)=>at is {} time&&time<=now&&(now-time).TotalMinutes<=maximumAgeMinutes;
  bool Source(string? id)=>!string.IsNullOrWhiteSpace(expectedDevice)&&id==expectedDevice;
  var freshPoll=Fresh(data?.Timestamp);
  return new(data?.SolarPowerValid==true&&data.SolarProduction>=0&&Fresh(data.SolarObservedAt)&&Source(data.SolarDeviceSn)?data.SolarProduction/1000d:null,
   freshPoll&&data?.LoadPowerValid==true&&data.LoadPower>=0?data.LoadPower/1000d:null,
   data?.GridPowerValid==true&&Fresh(data.GridObservedAt)&&Source(data.GridDeviceSn)?data.GridConsumption/1000d:null,
   freshPoll&&data?.BatteryPowerValid==true?data.BatteryPower/1000d:null,
   freshPoll&&data?.BatterySocValid==true?data.BatterySoc:null,data?.SolarObservedAt,data?.GridObservedAt,data?.Timestamp);
 }
}
