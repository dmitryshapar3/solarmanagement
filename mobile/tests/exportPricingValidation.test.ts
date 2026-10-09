import assert from "node:assert/strict";
import { test } from "node:test";
import { validPriceFeedUrl } from "../src/features/settings/exportPricingValidation";

test("custom feed only accepts HTTPS DNS URLs without credentials, fragments or nonstandard ports", () => {
  for (const value of ["https://example.com/prices.csv", "https://prices.example.com/a.xml?date=2026-10-09", "https://example.com:443/p.csv"]) assert.equal(validPriceFeedUrl(value), true, value);
  for (const value of ["", "http://example.com/a.csv", "https://127.0.0.1/a.csv", "https://[::1]/a.csv", "https://user:pass@example.com/a.csv", "https://host.local/a.csv", "https://example.com:444/a.csv", "https://example.com/a.csv#fragment", "https://example.com/\nfoo", "https://example.com/" + "x".repeat(2048)]) assert.equal(validPriceFeedUrl(value), false, value);
});
