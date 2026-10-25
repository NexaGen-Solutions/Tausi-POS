using Microsoft.Data.SqlClient;
using Tausi.Application.Setup;

namespace Tausi.IntegrationTests.Support;

/// <summary>
/// A small SambaPOS 5-like database on the test SQL Server: menu items with portions and price
/// tags, order tags (Cold / Warm), table and customer entities, and users with roles and PINs
/// (which the import must never read).
/// </summary>
public static class SambaPosDatabase
{
    public static async Task<SambaPosConnection> Create(SqlServerFixture sql, string name, bool withoutTagMaps = false)
    {
        await using (var master = new SqlConnection(sql.ConnectionFor("master")))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"IF DB_ID('{name}') IS NULL CREATE DATABASE [{name}]", master);
            await create.ExecuteNonQueryAsync();
        }
        await using var db = new SqlConnection(sql.ConnectionFor(name));
        await db.OpenAsync();
        var script = $"""
            CREATE TABLE MenuItems (Id int PRIMARY KEY, Name nvarchar(200) NOT NULL, GroupCode nvarchar(100) NULL, Barcode nvarchar(50) NULL, Tag nvarchar(50) NULL);
            CREATE TABLE MenuItemPortions (Id int PRIMARY KEY, Name nvarchar(100) NULL, MenuItemId int NOT NULL, Multiplier int NOT NULL DEFAULT 1);
            CREATE TABLE MenuItemPrices (Id int PRIMARY KEY, MenuItemPortionId int NOT NULL, PriceTag nvarchar(50) NULL, Price decimal(16,2) NOT NULL);
            CREATE TABLE OrderTagGroups (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL, MinSelectedItems int NULL, MaxSelectedItems int NULL);
            CREATE TABLE OrderTags (Id int PRIMARY KEY, OrderTagGroupId int NOT NULL, Name nvarchar(100) NOT NULL, Price decimal(16,2) NOT NULL DEFAULT 0);
            {(withoutTagMaps ? "" : "CREATE TABLE OrderTagMaps (Id int PRIMARY KEY, OrderTagGroupId int NOT NULL, MenuItemGroupCode nvarchar(100) NULL, MenuItemId int NULL);")}
            CREATE TABLE EntityTypes (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL, EntityName nvarchar(100) NULL);
            CREATE TABLE Entities (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL, EntityTypeId int NOT NULL);
            CREATE TABLE UserRoles (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL);
            CREATE TABLE Users (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL, PinCode nvarchar(50) NULL, UserRole_Id int NULL);
            CREATE TABLE ScreenMenuCategories (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL, ScreenMenuId int NOT NULL);
            CREATE TABLE ScreenMenuItems (Id int PRIMARY KEY, ScreenMenuCategoryId int NOT NULL, MenuItemId int NOT NULL, Name nvarchar(100) NULL);

            INSERT INTO MenuItems VALUES (1, 'Tusker', 'Beers', NULL, NULL), (2, 'Guinness Samba', 'Beers', NULL, NULL), (3, 'Pool Game', 'Games', NULL, NULL);
            INSERT INTO MenuItemPortions VALUES (11, 'Normal', 1, 1), (21, 'Small', 2, 1), (22, 'Large', 2, 1), (31, 'Normal', 3, 1);
            INSERT INTO MenuItemPrices VALUES (1, 11, NULL, 250), (2, 11, 'Happy Hour', 200), (3, 21, '', 200), (4, 22, NULL, 350), (5, 31, NULL, 40);
            INSERT INTO OrderTagGroups VALUES (5, 'Temperature', 1, 1);
            INSERT INTO OrderTags VALUES (51, 5, 'Cold', 0), (52, 5, 'Warm', 0);
            {(withoutTagMaps ? "" : "INSERT INTO OrderTagMaps VALUES (1, 5, 'Beers', 0);")}
            INSERT INTO EntityTypes VALUES (1, 'Tables', 'Table'), (2, 'Customers', 'Customer');
            INSERT INTO Entities VALUES (101, 'Patio 1', 1), (102, 'Patio 2', 1), (103, 'Patio 3', 1), (201, 'Kamau', 2);
            INSERT INTO UserRoles VALUES (1, 'Admin'), (2, 'Waiter'), (3, 'Cashier');
            INSERT INTO Users VALUES (1, 'Kioko', '1234', 1), (2, 'Akinyi', '5678', 2), (3, 'Otieno', '9012', 3);
            INSERT INTO ScreenMenuCategories VALUES (1, 'Drinks', 1);
            INSERT INTO ScreenMenuItems VALUES (1, 1, 1, NULL), (2, 1, 2, NULL);
            """;
        await using var command = new SqlCommand(script, db);
        await command.ExecuteNonQueryAsync();

        var b = new SqlConnectionStringBuilder(sql.ConnectionString);
        return new SambaPosConnection(b.DataSource, name, false, b.UserID, b.Password);
    }
}
