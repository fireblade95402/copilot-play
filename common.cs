using Azure;
using Azure.Data.Tables;
using System;

namespace MyCarbon.Common
{

    //Writing to Azure Table Storage
    public class CarbonCheckEntity : BaseTableEntity
    {
        public DateTime CreatedTime { get; set; }
        public int CarbonIntensity { get; set; }
        public bool CanChargeCar { get; set; }
    }

    public class BaseTableEntity : ITableEntity
    {
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }

    //Reading from the response from the Carbon Intensity API
    public class CarbonIntensity
    {
        public CarbonIntensityData[] data { get; set; }
    }

    public class CarbonIntensityData
    {
        public CarbonIntensityInterval intensity { get; set; }
    }

    public class CarbonIntensityInterval
    {
        public int actual { get; set; }
    }

}