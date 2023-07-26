using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using MyCarbon.Common;
using System.Net.Http;
using System;

namespace MyCarbon.Functions
{
    public class carbonCheck
    {
        private const int MaxCarbonIntensityRecords = 100;
        private const int threshold = 100;
        private static readonly HttpClient _httpClient = new HttpClient();


        
        [FunctionName("CarbonCheckTimer")]
        public static async Task Run([TimerTrigger("0 */30 * * * *")] TimerInfo myTimer, ILogger log,
            [Table("CarbonIntensityData")] IAsyncCollector<CarbonCheckEntity> carbonIntensityTable,
            [Table("CarbonIntensityData")] TableClient carbonIntensityTableQuery)
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            // Call the carbonCheck_Manual function
            var result = await carbonCheck_Manual(null, carbonIntensityTable, carbonIntensityTableQuery, log);

            log.LogInformation(result.ToString());
        }


        [FunctionName("carbonCheck_Manual")]
        public static async Task<IActionResult> carbonCheck_Manual(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            [Table("CarbonIntensityData")] IAsyncCollector<CarbonCheckEntity> carbonIntensityTable,
            [Table("CarbonIntensityData")] TableClient carbonIntensityTableQuery,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            try
            {
                // Call the Carbon Intensity API to get the latest data
                var response = await _httpClient.GetAsync("https://api.carbonintensity.org.uk/intensity/");
                response.EnsureSuccessStatusCode();
                var responseContent = await response.Content.ReadAsStringAsync();
                var responseData = JsonConvert.DeserializeObject<CarbonIntensity>(responseContent);

                // Get the latest carbon intensity value
                var latestIntensity = responseData.data[0].intensity.actual;

                // Get the latest intensity values from Azure Table Storage 
                var queryResult = await carbonIntensityTableQuery.QueryAsync<CarbonCheckEntity>().Where(x => x.PartitionKey == "CarbonIntensity").OrderByDescending(x => x.CreatedTime).Take(1).ToListAsync();

                if (queryResult != null && queryResult.Any())
                {
                    log.LogInformation("Carbon Intensity - Current: " + queryResult.Last().CarbonIntensity.ToString() + ", Previous: " + latestIntensity.ToString());
                    // If the latest value is the same as the last value in Azure Table Storage, return a message indicating that nothing has changed
                    if (queryResult.Any() && queryResult.First().CarbonIntensity == latestIntensity)
                    {
                        return new OkObjectResult(new { message = "Nothing has changed.", carbonIntensity = latestIntensity.ToString() });
                    }
                }

                // Save the latest carbon intensity value to Azure Table Storage
                var carbonIntensityEntity = new CarbonCheckEntity
                {
                    PartitionKey = "CarbonIntensity",
                    RowKey = System.DateTime.UtcNow.ToString("o"),
                    CreatedTime = System.DateTime.UtcNow,
                    CarbonIntensity = latestIntensity,
                    CanChargeCar = latestIntensity <= threshold
                };
                await carbonIntensityTable.AddAsync(carbonIntensityEntity);


                //delete oldest record if there are more than 100 records
                queryResult = await carbonIntensityTableQuery.QueryAsync<CarbonCheckEntity>().Where(x => x.PartitionKey == "CarbonIntensity").OrderByDescending(x => x.CreatedTime).Take(MaxCarbonIntensityRecords + 1).ToListAsync();
                log.LogInformation("Carbon Intensity - Number of records: " + queryResult.Count().ToString());
                if (queryResult.Count() >= MaxCarbonIntensityRecords)
                {
                    var oldestRecord = queryResult.Last();
                    await carbonIntensityTableQuery.DeleteEntityAsync(oldestRecord.PartitionKey, oldestRecord.RowKey);
                }

                // Return a message indicating whether it's okay to charge
                var message = latestIntensity <= threshold
                    ? "It's okay to charge your car."
                    : "It's not okay to charge your car.";
                return new OkObjectResult(new { message, carbonIntensity = latestIntensity.ToString() });
            }
            catch (HttpRequestException ex)
            {
                log.LogError(ex, "An error occurred while calling the Carbon Intensity API.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
            catch (JsonException ex)
            {
                log.LogError(ex, "An error occurred while parsing the Carbon Intensity API response.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }

        [FunctionName("GetCarbonIntensityGraph")]
        public static async Task<IActionResult> GetCarbonIntensityGraph(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            [Table("CarbonIntensityData")] TableClient carbonIntensityTable,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            try
            {
                // Get the latest 100 carbon intensity values from Azure Table Storage using current connectionstring
                var queryResult = await carbonIntensityTable.QueryAsync<CarbonCheckEntity>().Where(x => x.PartitionKey == "CarbonIntensity").OrderByDescending(x => x.CreatedTime).Take(MaxCarbonIntensityRecords).ToListAsync();

                // Create a string containing the data for the graph
                var graphData = "";
                foreach (var entity in queryResult)
                {
                    graphData += $"['{entity.RowKey}', {entity.CarbonIntensity}, {threshold}],";
                }
                graphData = graphData.TrimEnd(',');
                log.LogInformation("Carbon Intensity - Graph Data: " + graphData);
                // Return the graph html
                return new ContentResult
                {
                    ContentType = "text/html",
                    Content = $@"
                        <html>
                            <head>
                                <script type='text/javascript' src='https://www.gstatic.com/charts/loader.js'></script>
                                <script type='text/javascript'>
                                    google.charts.load('current', {{ 'packages': ['corechart'] }});
                                    google.charts.setOnLoadCallback(drawChart);
                                    function drawChart() {{
                                        var data = google.visualization.arrayToDataTable([
                                        ['Time', 'Carbon Intensity', 'Charge Car Threshold'],
                                        {graphData}
                                        ]);
                                        var options = {{
                                            title: 'Carbon Intensity',
                                            curveType: 'function',
                                            legend: {{ position: 'bottom' }},
                                            series: {{
                                                0: {{ axis: 'CarbonIntensity' }},
                                                1: {{ axis: 'Charge Car Threshold', type: 'line' }}
                                            }},
                                            axes: {{
                                                y: {{
                                                    CarbonIntensity: {{ label: 'Carbon Intensity' }},
                                                    Threshold: {{ label: 'Charge Car Threshold', ticks: [0, {threshold}], drawGridLines: false }}
                                                }}
                                            }}
                                        }};
                                        var chart = new google.visualization.LineChart(document.getElementById('curve_chart'));
                                        chart.draw(data, options);
                                    }}
                                </script>
                            </head>
                            <body>
                                <div id='curve_chart' style='width: 900px; height: 500px'></div>
                            </body>
                        </html>"
                };
            }
            catch (Exception ex)
            {
                log.LogError(ex, "An error occurred while querying the Carbon Intensity table.");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }

    }
}

