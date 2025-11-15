using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Waher.Content;

namespace Sensor
{
	/// <summary>
	/// Reads weather data from the Open Weather Map API
	/// </summary>
	/// <param name="ApiKey">Default API Key</param>
	/// <param name="Location">Default Location</param>
	/// <param name="Country">Default Country</param>
	public class OpenWeatherMapApi(string ApiKey, string Location, string Country)
	{
		private readonly string apiKey = ApiKey;
		private readonly string location = Location;
		private readonly string country = Country;

		/// <summary>
		/// Reads weather data using the default API Key from the default location & country.
		/// </summary>
		/// <returns>Weather data.</returns>
		public Task<WeatherInformation> GetData()
		{
			return this.GetData(this.location, this.country);
		}

		/// <summary>
		/// Reads weather data using the default API Key.
		/// </summary>
		/// <param name="Location">Location</param>
		/// <param name="Country">Country</param>
		/// <returns>Weather data.</returns>
		public Task<WeatherInformation> GetData(string Location, string Country)
		{
			return GetData(this.apiKey, Location, Country);
		}

		/// <summary>
		/// Reads weather data.
		/// </summary>
		/// <param name="ApiKey">API Key</param>
		/// <param name="Location">Location</param>
		/// <param name="Country">Country</param>
		/// <returns>Weather data.</returns>
		public static async Task<WeatherInformation> GetData(string ApiKey, string Location, string Country)
		{
			Uri Uri = new("http://api.openweathermap.org/data/2.5/weather?q=" + Location + "," + 
				Country + "&units=metric&APPID=" + ApiKey);

			ContentResponse Content = await InternetContent.GetAsync(Uri, new KeyValuePair<string, string>("Accept", "application/json"));
			Content.AssertOk();

			object Obj = Content.Decoded;
			WeatherInformation Result = new();
			Result.Timestamp = Result.Readout = DateTime.UtcNow;

			if (Obj is not Dictionary<string, object> Response)
				throw new Exception("Unexpected response from API.");

			if (Response.TryGetValue("dt", out Obj) && Obj is int dt)
				Result.Timestamp = JSON.UnixEpoch.AddSeconds(dt);

			if (Response.TryGetValue("name", out Obj) && Obj is string Name)
				Result.Name = Name;

			if (Response.TryGetValue("id", out Obj))
				Result.Id = Obj.ToString();

			if (Response.TryGetValue("timezone", out Obj) && Obj is int TimeZone)
				Result.TimeZone = TimeSpan.FromSeconds(TimeZone);

			if (Response.TryGetValue("visibility", out Obj) && Obj is int Visibility)
				Result.VisibilityMeters = Visibility;

			if (Response.TryGetValue("coord", out Obj) && Obj is Dictionary<string, object> Coord)
			{
				if (Coord.TryGetValue("lon", out Obj) && Obj is double Lon)
					Result.LongitudeDegrees = Lon;

				if (Coord.TryGetValue("lat", out Obj) && Obj is double Lat)
					Result.LatitudeDegrees = Lat;
			}

			if (Response.TryGetValue("main", out Obj) && Obj is Dictionary<string, object> Main)
			{
				if (Main.TryGetValue("temp", out Obj) && Obj is double Temp)
					Result.TemperatureCelcius = Temp;

				if (Main.TryGetValue("feels_like", out Obj) && Obj is double FeelsLike)
					Result.FeelsLikeCelcius = FeelsLike;

				if (Main.TryGetValue("temp_min", out Obj) && Obj is double TempMin)
					Result.TemperatureMinCelcius = TempMin;

				if (Main.TryGetValue("temp_max", out Obj) && Obj is double TempMax)
					Result.TemperatureMaxCelcius = TempMax;

				if (Main.TryGetValue("pressure", out Obj) && Obj is int Pressure)
					Result.PressureHPa = Pressure;

				if (Main.TryGetValue("humidity", out Obj) && Obj is int Humidity)
					Result.HumidityPercent = Humidity;
			}

			if (Response.TryGetValue("wind", out Obj) && Obj is Dictionary<string, object> Wind)
			{
				if (Wind.TryGetValue("speed", out Obj) && Obj is double Speed)
					Result.WindSpeedMPerS = Speed;

				if (Wind.TryGetValue("deg", out Obj) && Obj is int Deg)
					Result.WindDirectionDegrees = Deg;
			}

			if (Response.TryGetValue("clouds", out Obj) && Obj is Dictionary<string, object> Clouds)
			{
				if (Clouds.TryGetValue("all", out Obj) && Obj is int All)
					Result.CloudinessPercent = All;
			}

			if (Response.TryGetValue("sys", out Obj) && Obj is Dictionary<string, object> Sys)
			{
				if (Sys.TryGetValue("id", out Obj) && Obj is int WeatherId)
					Result.WeatherId = WeatherId;

				if (Sys.TryGetValue("country", out Obj) && Obj is string Country2)
					Result.Country = Country2;

				if (Sys.TryGetValue("sunrise", out Obj) && Obj is int Sunrise)
					Result.Sunrise = JSON.UnixEpoch.AddSeconds(Sunrise);

				if (Sys.TryGetValue("sunset", out Obj) && Obj is int Sunset)
					Result.Sunset = JSON.UnixEpoch.AddSeconds(Sunset);
			}

			if (Response.TryGetValue("weather", out Obj) &&
				Obj is Array WeatherArray &&
				WeatherArray.Length == 1 &&
				WeatherArray.GetValue(0) is Dictionary<string, object> Weather)
			{
				if (Weather.TryGetValue("main", out Obj) && Obj is string Main2)
					Result.Weather = Main2;

				if (Weather.TryGetValue("description", out Obj) && Obj is string Description)
					Result.Description = Description;

				if (Weather.TryGetValue("icon", out Obj) && Obj is string Icon)
					Result.IconUrl = "http://openweathermap.org/img/wn/" + Icon + "@2x.png";
			}

			return Result;
		}
	}
}
