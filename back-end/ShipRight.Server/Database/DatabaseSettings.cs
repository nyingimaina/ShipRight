namespace ShipRight.Database;

public class DatabaseSettings
{
    public string Server { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "shipright";
    public string UserId { get; set; } = "root";
    public string Password { get; set; } = "";
    public string ConnectionString => $"Server={Server};Port={Port};Database={Database};User Id={UserId};Password={Password};Charset=utf8;ConvertZeroDateTime=True;";
}
