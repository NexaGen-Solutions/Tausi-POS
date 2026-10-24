using Tausi.Domain.Common;

namespace Tausi.Domain.Ledger;

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Income,
    Expense,
}

public class LedgerAccount : VersionedEntity
{
    /// <summary>Stable code used by postings, e.g. "CASH".</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public AccountType Type { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>Codes of the accounts every business has. Postings refer to accounts by code.</summary>
public static class LedgerAccountCodes
{
    public const string Cash = "CASH";
    public const string MPesa = "MPESA";
    public const string Bank = "BANK";
    public const string CardClearing = "CARD_CLEARING";
    public const string Sales = "SALES";
    public const string VatPayable = "VAT_PAYABLE";
    public const string Stock = "STOCK";
    public const string CostOfSales = "COST_OF_SALES";
    public const string StockLoss = "STOCK_LOSS";
    public const string Expenses = "EXPENSES";
    public const string SupplierPayables = "SUPPLIER_PAYABLES";
    public const string Rounding = "ROUNDING";
    public const string Safe = "SAFE";
    public const string CashOverShort = "CASH_OVER_SHORT";
    public const string OpeningBalances = "OPENING_BALANCES";

    public static readonly IReadOnlyList<(string Code, string Name, AccountType Type)> Defaults =
    [
        (Cash, "Cash", AccountType.Asset),
        (MPesa, "M-Pesa", AccountType.Asset),
        (Bank, "Bank", AccountType.Asset),
        (CardClearing, "Card clearing", AccountType.Asset),
        (Sales, "Sales", AccountType.Income),
        (VatPayable, "VAT payable", AccountType.Liability),
        (Stock, "Stock", AccountType.Asset),
        (CostOfSales, "Cost of sales", AccountType.Expense),
        (StockLoss, "Stock loss", AccountType.Expense),
        (Expenses, "Expenses", AccountType.Expense),
        (SupplierPayables, "Supplier payables", AccountType.Liability),
        (Rounding, "Rounding", AccountType.Income),
        (Safe, "Safe", AccountType.Asset),
        (CashOverShort, "Cash over/short", AccountType.Expense),
        (OpeningBalances, "Opening balances", AccountType.Equity),
    ];
}
