using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Tausi.Application.Settings;
using Tausi.Domain.Settings;

namespace Tausi.Tests.Settings;

public class SettingsRegistryTests
{
    /// <summary>The keys Task 10 lists, plus the ones earlier tasks added.</summary>
    private static readonly string[] KeyList =
    [
        "business.name", "business.logo", "business.address", "business.phone", "business.vatRate", "business.currency",
        "business.timeZone", "business.receiptFooter",
        "pos.idleLogoutEnabled", "pos.idleLogoutMinutes", "pos.logoutAfterSettle", "pos.tableIdleMinutes",
        "pos.approvalExpiryMinutes", "pos.approvalsFromOwnCounter", "pos.approvalChime",
        "print.mode", "print.logoOnBill", "print.poweredByFooter", "print.showVoidsOnReceipt",
        "money.rounding", "payments.mpesaCodeRequired", "payments.recordBroughtBy", "tickets.closeVoidedWithoutReceipt",
        "cash.defaultFloat", "cash.denominations", "cashup.blind", "stock.returnOnVoidIfNotServed",
    ];

    public static readonly TheoryData<string> Keys = new(KeyList);

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_key_exists_with_a_label_help_and_a_default_that_passes_its_own_check(string key)
    {
        var d = SettingsRegistry.Find(key);
        d.Should().NotBeNull();
        d!.Label.Should().NotBeNullOrWhiteSpace();
        d.Help.Should().NotBeNullOrWhiteSpace();
        if (!d.Editable) return;
        d.Default.Should().NotBeNull();
        if (key != SettingsRegistry.BusinessName)
            SettingsRegistry.Normalize(d, JsonSerializer.SerializeToElement(d.Default), out _).Should().BeNull();
    }

    [Fact]
    public void The_registry_has_no_keys_beyond_the_list_and_each_once()
    {
        SettingsRegistry.All.Select(d => d.Key).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(KeyList);
    }

    [Fact]
    public void The_rules_are_the_wireframes_switches_in_order()
    {
        SettingsRegistry.All.Where(d => d.Rule != null).OrderBy(d => d.Rule).Select(d => d.Key).Should().Equal(
            "print.mode", "pos.approvalsFromOwnCounter", "payments.recordBroughtBy", "print.showVoidsOnReceipt",
            "tickets.closeVoidedWithoutReceipt", "stock.returnOnVoidIfNotServed", "payments.mpesaCodeRequired", "pos.idleLogoutEnabled");
    }

    [Fact]
    public void Defaults_apply_when_nothing_is_stored()
    {
        var v = SettingsRegistry.Read(new BusinessSettings { OtherJson = "{}" });

        v.Bool(SettingsRegistry.PosIdleLogoutEnabled).Should().BeTrue();
        v.Int(SettingsRegistry.PosIdleLogoutMinutes).Should().Be(10);
        v.Bool(SettingsRegistry.PosApprovalsFromOwnCounter).Should().BeTrue();
        v.Bool(SettingsRegistry.PrintShowVoidsOnReceipt).Should().BeFalse();
        v.Bool(SettingsRegistry.PaymentsMpesaCodeRequired).Should().BeTrue();
        v.Bool(SettingsRegistry.StockReturnOnVoidIfNotServed).Should().BeTrue();
        v.IntList(SettingsRegistry.CashDenominations).Should().Equal(1000, 500, 200, 100, 50);
        SettingsRegistry.Read(null).Decimal(SettingsRegistry.BusinessVatRate).Should().Be(16m);
    }

    [Theory]
    [InlineData("pos.idleLogoutMinutes", "0", "Enter a whole number from 1 to 120.")]
    [InlineData("pos.idleLogoutMinutes", "121", "Enter a whole number from 1 to 120.")]
    [InlineData("pos.idleLogoutMinutes", "1.5", "Enter a whole number from 1 to 120.")]
    [InlineData("pos.idleLogoutEnabled", "\"yes\"", "Choose on or off.")]
    [InlineData("business.vatRate", "100", "Enter a number from 0 to 99.99.")]
    [InlineData("business.vatRate", "16.555", "Use at most 2 decimal places.")]
    [InlineData("business.currency", "\"KSH1\"", "Use at most 3 characters.")]
    [InlineData("business.currency", "\"K1\"", "Use a 3-letter code, e.g. KES.")]
    [InlineData("business.name", "\"  \"", "Business name is required.")]
    [InlineData("business.timeZone", "\"Mars/Olympus\"", "Choose a time zone from the list, e.g. Africa/Nairobi.")]
    [InlineData("print.mode", "\"TwoButtons\"", "Choose one of: OneButton, SendAndBill.")]
    [InlineData("cash.denominations", "[]", "Enter note values, e.g. 1000, 500, 200, 100, 50.")]
    [InlineData("cash.denominations", "[1000, -5]", "Enter note values, e.g. 1000, 500, 200, 100, 50.")]
    [InlineData("cash.defaultFloat", "-1", "Enter a number from 0 to 10,000,000.")]
    public void Out_of_range_and_wrong_values_are_refused_with_a_message(string key, string json, string message)
    {
        SettingsRegistry.Normalize(SettingsRegistry.Find(key)!, JsonDocument.Parse(json).RootElement, out _).Should().Be(message);
    }

    [Fact]
    public void Values_are_stored_clean_text_trimmed_currency_upper_case_notes_sorted()
    {
        Clean("business.name", "\"  Mau's Village Bar \"")!.GetValue<string>().Should().Be("Mau's Village Bar");
        Clean("business.currency", "\"kes\"")!.GetValue<string>().Should().Be("KES");
        Clean("print.mode", "\"sendandbill\"")!.GetValue<string>().Should().Be("SendAndBill");
        Clean("cash.denominations", "[50, 1000, 50, 200]")!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(1000, 200, 50);
    }

    private static JsonNode? Clean(string key, string json)
    {
        SettingsRegistry.Normalize(SettingsRegistry.Find(key)!, JsonDocument.Parse(json).RootElement, out var clean).Should().BeNull();
        return clean;
    }

    [Fact]
    public void Writing_puts_columns_in_columns_and_the_rest_in_their_place_in_other_json()
    {
        var row = new BusinessSettings { OtherJson = """{"pos":{"tableIdleMinutes":45},"custom":{"x":1}}""" };

        SettingsRegistry.Write(row, SettingsRegistry.PrintModeKey, "SendAndBill");
        SettingsRegistry.Write(row, SettingsRegistry.PosIdleLogoutEnabled, false);
        SettingsRegistry.Write(row, SettingsRegistry.PaymentsMpesaCodeRequired, false);

        row.PrintMode.Should().Be(PrintMode.SendAndBill);
        var other = JsonNode.Parse(row.OtherJson)!;
        other["pos"]!["tableIdleMinutes"]!.GetValue<int>().Should().Be(45, "what was there stays");
        other["pos"]!["idleLogoutEnabled"]!.GetValue<bool>().Should().BeFalse();
        other["payments"]!["mpesaCodeRequired"]!.GetValue<bool>().Should().BeFalse();
        other["custom"]!["x"]!.GetValue<int>().Should().Be(1);
        SettingsRegistry.Read(row).Bool(SettingsRegistry.PaymentsMpesaCodeRequired).Should().BeFalse();
    }
}
