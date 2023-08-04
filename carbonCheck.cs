        // Azure function is triggered by an HTTP request
        // Check the carbon intensity of the uk grid if I should charge my car calling the following api:  https://api.carbonintensity.org.uk/intensity/
        // If it's below 100g/kWh, return a message saying it's okay to charge
        // If it's above 100g/kWh, return a message saying it's not okay to charge
        // take the response from the API in the following json format:
        // {
        //     "data": [
        //         {
        //             "from": "2023-07-19T10:30Z",
        //             "to": "2023-07-19T11:00Z",
        //             "intensity": {
        //                 "forecast": 187,
        //                 "actual": 186,
        //                 "index": "moderate"
        //             }
        //         }
        //     ]
        // }
        // and return a message in the following format:
        // {
        //     "message": "It's okay to charge your car"
        // }
        // and save the json responses to Azure Storage Table for later analytics and visualisation using an outbound binding
        // https://docs.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-table-output?tabs=csharp
        
using System.Linq;
using System.Threading.Tasks;
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
using System.Collections.Generic;

namespace MyCarbon.Functions
{
    public class carbonCheck
    {
        private static int MaxCarbonIntensityRecords = System.Environment.GetEnvironmentVariable("MaxCarbonIntensityRecords") != null ? int.Parse(System.Environment.GetEnvironmentVariable("MaxCarbonIntensityRecords")) : 100;
        private static int threshold = System.Environment.GetEnvironmentVariable("CarChargeThreshold") != null ? int.Parse(System.Environment.GetEnvironmentVariable("CarChargeThreshold")) : 100;
        private static string Environment = System.Environment.GetEnvironmentVariable("Environment") != null && System.Environment.GetEnvironmentVariable("Environment") != "Production" ? System.Environment.GetEnvironmentVariable("Environment") : "";
        private static readonly HttpClient _httpClient = new HttpClient();

        [FunctionName("CarbonCheckTimer")]
        public static async Task Run([TimerTrigger("%CarbonCheckCron%")] TimerInfo myTimer, ILogger log,
            [Table("%AzureStorageTableName%", Connection = "AzureStorageTableConnection")] IAsyncCollector<CarbonCheckEntity> carbonIntensityTable,
            [Table("%AzureStorageTableName%", Connection = "AzureStorageTableConnection")] TableClient carbonIntensityTableQuery)
        {
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            // Call the carbonCheck_Manual function
            var result = await carbonCheck_Manual(null, carbonIntensityTable, carbonIntensityTableQuery, log);

            log.LogInformation(result.ToString());
        }

        [FunctionName("carbonCheck_Manual")]
        public static async Task<IActionResult> carbonCheck_Manual(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            [Table("%AzureStorageTableName%", Connection = "AzureStorageTableConnection")] IAsyncCollector<CarbonCheckEntity> carbonIntensityTable,
            [Table("%AzureStorageTableName%", Connection = "AzureStorageTableConnection")] TableClient carbonIntensityTableQuery,
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
            [Table("%AzureStorageTableName%", Connection = "AzureStorageTableConnection")] TableClient carbonIntensityTable,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            try
            {
                // Get the latest 100 carbon intensity values from Azure Table Storage using current connectionstring
                var queryResult = await carbonIntensityTable.QueryAsync<CarbonCheckEntity>().Where(x => x.PartitionKey == "CarbonIntensity").OrderByDescending(x => x.CreatedTime).Take(MaxCarbonIntensityRecords).ToListAsync();

                //reserve the order of the records
                queryResult.Reverse();

                // Create a string containing the data for the graph
                var graphData = "";
                foreach (var entity in queryResult)
                {
                    graphData += $"['{entity.RowKey}', {entity.CarbonIntensity}, {threshold}],";
                }
                graphData = graphData.TrimEnd(',');

                // Return the graph html
                return new ContentResult
                {
                    ContentType = "text/html",
                    Content = $@"
                        <html>
                            <head>
                                <script type='text/javascript' src='https://www.gstatic.com/charts/loader.js'></script>
                                <script type='text/javascript'>
                                    google.charts.load('current', {{ 'packages': ['corechart', 'table'] }});
                                    google.charts.setOnLoadCallback(drawChart);
                                    function drawChart() {{
                                        var data = google.visualization.arrayToDataTable([
                                        ['Time', 'Carbon Intensity', 'Charge Car Threshold'],
                                        {graphData}
                                        ]);
                                        var options = {{
                                            title: 'Carbon Intensity vs Charge Car Threshold',
                                            description: 'Last {MaxCarbonIntensityRecords} records with threshold of {threshold}',
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

                                        var chartDescription = document.getElementById('chart_description');
                                        chartDescription.innerHTML = options.description; // Set the chart description
                                     
                                        var table = new google.visualization.Table(document.getElementById('table_div'));
                                        table.draw(data, {{
                                            showRowNumber: true,
                                            page: 'enable',
                                            pageSize: 20,
                                            sortColumn: 0,
                                            sortAscending: false

                                        }});
                                    }}
                                </script>
                            </head>
                            <body>
                                <H1>{Environment}</H1>
                                <H2>Carbon Intensity vs Charge Car Threshold v3(test slot)</H2>
                                <H3><div id='chart_description'></div></H3>
                                <div id='curve_chart' style='width: 900px; height: 500px'></div>
                                <br/>
                                <div id='table_div'></div>
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



        [FunctionName("ShowCarbonGraph")]
        public static async Task<IActionResult> ShowCarbonGraph(

            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            [Table("%AzureStorageTableName%", Connection = "AzureStorageTableConnection")] TableClient carbonIntensityTable,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            // Get the carbon intensity from the Azure Table Storage.
            var queryResult = await carbonIntensityTable.QueryAsync<CarbonCheckEntity>().Where(x => x.PartitionKey == "CarbonIntensity").OrderByDescending(x => x.CreatedTime).Take(100).ToListAsync();

            var carbonChecks = queryResult.OrderByDescending(x => x.Timestamp).ToList();

            // Create the data for the graph.
            List<string> labels = queryResult.Select(c => c.CreatedTime.ToString("yyyy-MM-dd HH:mm:ss")).ToList();
            List<int> values = queryResult.Select(c => c.CarbonIntensity).ToList();
            string data = JsonConvert.SerializeObject(new { labels, values });
            log.LogInformation($"Data: {data}");
            // Create the HTML for the graph.
            string html = $@"
            <html>
            <head>
                <script src='https://cdn.jsdelivr.net/npm/chart.js'></script>
            </head>
            <body>
                <canvas id='myChart'></canvas>
                <script>
                    var data = {data};
                    var ctx = document.getElementById('myChart').getContext('2d');
                    var myChart = new Chart(ctx, {{
                        type: 'line',
                        data: {{
                            labels: data.labels,
                            datasets: [{{
                                label: 'Carbon Intensity',
                                data: data.values,
                                fill: false,
                                borderColor: 'rgb(75, 192, 192)',
                                tension: 0.1
                            }},
                            {{
                                label: 'Threshold',
                                data: Array(data.values.length).fill({threshold}),
                                fill: true,
                                borderColor: 'rgb(255, 0, 0)',
                                borderDash: [5, 5],
                                tension: 0.1
                            }}]
                        }},
                        options: {{
                            scales: {{
                                y: {{
                                    beginAtZero: true
                                }}
                            }}
                        }}
                    }});
                </script>
            </body>
            </html>";

            return new ContentResult
            {
                Content = html,
                ContentType = "text/html",
                StatusCode = 200
            };
        }




    }
}

