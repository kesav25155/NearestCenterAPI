using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace MyNearestCentersApi.Services
{
    public class GeoCoordinate
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public class Center
    {
        public string? Name { get; set; }
        public string? Address { get; set; }
        public GeoCoordinate? Coordinates { get; set; }
        public int IsCenter { get; set; }
        public int SiteId { get; set; }
    }

    public class WaitingTimeResponse
    {
        public List<WaitingTimeData> DataValues { get; set; } = new List<WaitingTimeData>();
    }

    public class WaitingTimeData
    {
        public int TotalOP { get; set; }
        public string? UpdatedTime { get; set; }
    }

    public class NearestCentersService
    {
        private readonly ILogger<NearestCentersService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _connectionString;
        private static readonly SemaphoreSlim _rateLimitSemaphore = new SemaphoreSlim(1, 1);

        public NearestCentersService(ILogger<NearestCentersService> logger, HttpClient httpClient, string connectionString)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "NearestCentersApp/1.0 (support@nearestcentersapi.com)");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task<string> GetNearestCentersAsync(string userAddress)
        {
            try
            {
                var userCoord = await GetCoordinatesFromAddressAsync(userAddress);
                if (userCoord == null)
                    return JsonSerializer.Serialize(new { error = "Could not geocode the provided address" });

                var validCenters = await GetCentersFromDatabaseAsync();
                if (!validCenters.Any())
                    return JsonSerializer.Serialize(new { error = "No valid centers available" });

                var nearbyCenters = validCenters
                    .Where(c => c.Coordinates != null)
                    .Select(c => new
                    {
                        c.Name,
                        c.Address,
                        Distance = CalculateHaversineDistance(userCoord, c.Coordinates!),
                        TravelTimes = CalculateTravelTimes(CalculateHaversineDistance(userCoord, c.Coordinates!), c.IsCenter, c.SiteId).Result
                    })
                    .Where(c => c.Distance <= 15)
                    .ToList();

                if (!nearbyCenters.Any())
                    return JsonSerializer.Serialize(new { message = "No centers found within 15 km" });

                return JsonSerializer.Serialize(new
                {
                    centersDistance = nearbyCenters
                        .OrderBy(c => c.Distance)
                        .Take(2)
                        .Select(c => new
                        {
                            c.Name,
                            c.Address,
                            DistanceKm = Math.Round(c.Distance, 2)
                        }),
                    centersTime = nearbyCenters
                        .OrderBy(c => c.TravelTimes.TravelTime + c.TravelTimes.WaitingTime)
                        .Take(2)
                        .Select(c => new
                        {
                            c.Name,
                            c.Address,
                            DistanceKm = Math.Round(c.Distance, 2),
                            TravelTimeHrs = Math.Round(c.TravelTimes.TravelTime / 60, 2),
                            WaitingTimeHrs = Math.Round(c.TravelTimes.WaitingTime / 60, 2),
                            TotalTimeHrs = Math.Round((c.TravelTimes.TravelTime + c.TravelTimes.WaitingTime) / 60, 2)
                        })
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = $"An error occurred: {ex.Message}" });
            }
        }

        private async Task<(double TravelTime, double WaitingTime)> CalculateTravelTimes(double distance, int isCenter, int siteId)
        {
            const double travelSpeed = 20.0;
            const double trafficFactor = 1.3;
            double baseTravelTime = (distance / travelSpeed) * 60;
            double travelTimeWithTraffic = baseTravelTime * trafficFactor;
            double waitingTime;

            if (isCenter == 1)
            {
                waitingTime = 10.0;
            }
            else
            {
                var apiResponse = await GetWaitingTimeFromApiAsync(siteId);
                int totalOP = apiResponse?.DataValues.FirstOrDefault()?.TotalOP ?? 1; // Fallback to 1 patient if API fails
                waitingTime = totalOP * 60.0; // 60 minutes per patient
            }

            return (TravelTime: travelTimeWithTraffic, WaitingTime: waitingTime);
        }

        private async Task<WaitingTimeResponse?> GetWaitingTimeFromApiAsync(int siteId)
        {
            await _rateLimitSemaphore.WaitAsync();
            try
            {
                string url = "http://localhost:5190/api/centers/wait";
                var requestBody = new { siteId };
                var requestContent = new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json"
                );
                var response = await _httpClient.PostAsync(url, requestContent);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<WaitingTimeResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return result;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error fetching waiting time from API for siteId: {SiteId}", siteId);
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error deserializing waiting time API response for siteId: {SiteId}", siteId);
                return null;
            }
            finally
            {
                await Task.Delay(1000);
                _rateLimitSemaphore.Release();
            }
        }

        private async Task<List<Center>> GetCentersFromDatabaseAsync()
        {
            var centers = new List<Center>();
            try
            {
                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new NpgsqlCommand("SELECT id AS SiteId, SiteName, SiteLocation, Latitude, Longitude, IsCenter FROM Centers", conn);
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    centers.Add(new Center
                    {
                        SiteId = reader.GetInt32(0),
                        Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Address = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Coordinates = new GeoCoordinate
                        {
                            Latitude = reader.GetDouble(3),
                            Longitude = reader.GetDouble(4)
                        },
                        IsCenter = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                    });
                }
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "Error retrieving centers from database");
            }
            return centers;
        }

        private async Task<GeoCoordinate?> GetCoordinatesFromAddressAsync(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return null;

            var pincodeRegex = new Regex(@"\b\d{6}\b");
            var pincodeMatch = pincodeRegex.Match(address);
            var pincode = pincodeMatch.Success ? pincodeMatch.Value : null;

            var addressWords = Regex.Split(address.Trim(), @"\s*,\s*|\s+").Where(w => !string.IsNullOrEmpty(w)).ToList();
            if (pincode != null)
                addressWords = addressWords.Where(w => w != pincode).ToList();
            var currentQueryWords = new List<string>(addressWords);

            try
            {
                if (currentQueryWords.Count < 3 && pincode != null)
                {
                    var result = await FetchNominatimResultAsync(pincode);
                    if (result != null)
                        return ParseCoordinates(result);
                }

                while (currentQueryWords.Count > 0)
                {
                    var currentQuery = string.Join(" ", currentQueryWords) + (pincode != null ? $" {pincode}" : "");
                    var result = await FetchNominatimResultAsync(currentQuery);
                    if (result != null)
                        return ParseCoordinates(result);
                    currentQueryWords = currentQueryWords.Skip(1).ToList();
                }

                if (pincode != null && addressWords.Count >= 3)
                {
                    var result = await FetchNominatimResultAsync(pincode);
                    if (result != null)
                        return ParseCoordinates(result);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error geocoding address");
                return null;
            }
        }

        private async Task<NominatimResult?> FetchNominatimResultAsync(string query)
        {
            await _rateLimitSemaphore.WaitAsync();
            try
            {
                string encodedQuery = Uri.EscapeDataString(query.Replace("#", "").Replace("&", ""));
                string url = $"https://nominatim.openstreetmap.org/search?q={encodedQuery}&format=json&limit=1&countrycodes=in";
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var results = JsonSerializer.Deserialize<List<NominatimResult>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return results?.FirstOrDefault();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error fetching Nominatim result");
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error deserializing Nominatim result");
                return null;
            }
            finally
            {
                await Task.Delay(1000);
                _rateLimitSemaphore.Release();
            }
        }

        private GeoCoordinate? ParseCoordinates(NominatimResult result)
        {
            if (result?.Lat == null || result.Lon == null)
                return null;

            try
            {
                var coord = new GeoCoordinate
                {
                    Latitude = double.Parse(result.Lat, System.Globalization.CultureInfo.InvariantCulture),
                    Longitude = double.Parse(result.Lon, System.Globalization.CultureInfo.InvariantCulture)
                };
                return coord;
            }
            catch (FormatException ex)
            {
                _logger.LogError(ex, "Error parsing coordinates");
                return null;
            }
        }

        private double CalculateHaversineDistance(GeoCoordinate coord1, GeoCoordinate coord2)
        {
            const double R = 6371;
            var lat1 = DegreesToRadians(coord1.Latitude);
            var lat2 = DegreesToRadians(coord2.Latitude);
            var deltaLat = DegreesToRadians(coord2.Latitude - coord1.Latitude);
            var deltaLon = DegreesToRadians(coord2.Longitude - coord1.Longitude);

            var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) +
                    Math.Cos(lat1) * Math.Cos(lat2) *
                    Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180;
        }
    }

    public class NominatimResult
    {
        public string? Lat { get; set; }
        public string? Lon { get; set; }
    }
}