using System;

namespace Sensor
{
	/// <summary>
	/// Contains information about the weather at one location.
	/// </summary>
	public class WeatherInformation
	{
		/// <summary>
		/// Timestamp of readout
		/// </summary>
		public DateTime Readout { get; set; }

		/// <summary>
		/// Timestamp of weather
		/// </summary>
		public DateTime Timestamp { get; set; }

		/// <summary>
		/// Name of location
		/// </summary>
		public string Name { get; set; }

		/// <summary>
		/// ID of location
		/// </summary>
		public string Id { get; set; }

		/// <summary>
		/// Time zone
		/// </summary>
		public TimeSpan? TimeZone { get; set; }

		/// <summary>
		/// Visibility, in meters
		/// </summary>
		public int? VisibilityMeters { get; set; }

		/// <summary>
		/// Longitude of location, in degrees
		/// </summary>
		public double? LongitudeDegrees { get; set; }

		/// <summary>
		/// Latitude of location, in degrees
		/// </summary>
		public double? LatitudeDegrees { get; set; }

		/// <summary>
		/// Temperature, in Celcius
		/// </summary>
		public double? TemperatureCelcius { get; set; }

		/// <summary>
		/// Minimum Temperature, in Celcius
		/// </summary>
		public double? TemperatureMinCelcius { get; set; }

		/// <summary>
		/// Maximum Temperature, in Celcius
		/// </summary>
		public double? TemperatureMaxCelcius { get; set; }

		/// <summary>
		/// Temperature, as it would feel, in Celcius
		/// </summary>
		public double? FeelsLikeCelcius { get; set; }

		/// <summary>
		/// Pressure, in hPa
		/// </summary>
		public int? PressureHPa { get; set; }

		/// <summary>
		/// Humidity, in %
		/// </summary>
		public int? HumidityPercent { get; set; }

		/// <summary>
		/// Wind Speed, in m/s
		/// </summary>
		public double? WindSpeedMPerS { get; set; }

		/// <summary>
		/// Wind Direction, in degrees
		/// </summary>
		public int? WindDirectionDegrees { get; set; }

		/// <summary>
		/// Cloudiness, in %
		/// </summary>
		public int? CloudinessPercent { get; set; }

		/// <summary>
		/// ID of reported weather
		/// </summary>
		public int? WeatherId { get; set; }

		/// <summary>
		/// Country
		/// </summary>
		public string Country { get; set; }

		/// <summary>
		/// Sunrise
		/// </summary>
		public DateTime? Sunrise { get; set; }

		/// <summary>
		/// Sunset
		/// </summary>
		public DateTime? Sunset { get; set; }

		/// <summary>
		/// Weather as a string
		/// </summary>
		public string Weather { get; set; }

		/// <summary>
		/// Description of weather
		/// </summary>
		public string Description { get; set; }

		/// <summary>
		/// URL of weather Icon
		/// </summary>
		public string IconUrl { get; set; }
	}
}
