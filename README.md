<sup>***Looking for [IOS Apple App Store](https://github.com/ANU-CHEEKI-BREEKI/app-store-connect-prices-localizer) analog?***</sup>

---

Have you ever created ***IAPs*** (In-App Purchases) for your app on Google Play?

Have you thought about ***setting regional prices*** to make purchases more accessible in countries with lower purchasing power?

Doing this ***manually***, even for a single IAP, ***is a nightmare...***

In this repository, there is a program that will help you ***automatically*** update all prices according to your *regional pricing template*.

---

### ATTENTION

``THE PROJECT WAS UPDATED TO USE NEW GOOGLE APIS FOR Onetimeproducts``
<br><br>
``but, since i (as Unity game-dev) dont use IAP purchase options, and use only the Default purchase option, this project designed to work only with single "LegacyCompatible"/"Backward compatible" purchase option``

<img src="image.png" alt="drawing" width="400"/>

---

### How to use

One command:

    dotnet run localize

And all prices will be localized!

**Or you can run the program without parameters. It will display a more detailed help in the console.**


**You also need to set up `../config.json` file**

    {
        "PackageName": "com.MyApp.Package",
        "CredentialsFilePath": "./oauth_client_credentials.json",
        "DefaultPricesFilePath": "./default-prices.json",
        "DefaultCurrency": "USD"
    }

`CredentialsFilePath` and `DefaultPricesFilePath` are relative to `config.json`

**About Credentials [HERE](#The-pain-in-the-ass)**

There are example of default-prices.json<br>
Prices are in `DefaultCurrency` specified in `config.json` - in this example its (USD) 

    {
        "crystals_1": 10,
        "crystals_6": 50,
    }

---

### How it works

In the file `localized-prices-template.json`, there are multipliers for each country's prices.

    {
        "AE": 0.85,
        "AT": 1.0,
        "AU": 1.0,
        "BD": 0.3,
        "BE": 1.0,
        "BG": 0.6,
        "BH": 0.85
        //...
    }

- The program retrieves a list of all IAPs using the [Google Play Developer APIs](https://developers.google.com/android-publisher), resets their prices to the default price (just like you can do manually in the Google Play Console by clicking on "Update Exchange Rate").
- Then, the program multiplies the local prices by the corresponding multipliers from the `localized-prices-template.json` file.
- After that, the prices are rounded.
- Then, 0.01 is subtracted from the price to make round prices like `10$` become `9.99$`.
- Finally, the program uses the [Google Play Developer APIs](https://developers.google.com/android-publisher) to update the IAPs in your project on the Google Play Console.

---

**Waring**

Because of rounding - small prices can stay unchanged (0.99 usd became 0.99 usd).
To fix this is required to ask google apis to convert localized price (0.99 * 72% for example) to target currency. This will make sure price a valid. But thats required more work and will take mre time for program to execute. I dont care about this to much, so i did not implement this yet.

---

Google Console does not allow decimal prices for some countries, so the `round-prices-for.json` file contains exceptions. For these countries, the prices will be rounded (no more `9.99`, only `10`).

Currently, the list of exceptions is relatively small:

    ["CI", "CL", "CM", "IL", "JP", "KR", "PY", "SN", "VN"]

---

### The pain in the ass

To access your project through [Google Play Developer APIs](https://developers.google.com/android-publisher), ***you need to create a desktop OAuth client*** in your [Google Cloud Console](https://console.cloud.google.com/apis/credentials) for your project and ensure that the project in [Google Cloud Console](https://console.cloud.google.com/apis/credentials) is ***either live*** or ***you are a tester***.

Then download the **`Client secrets`** of the created OAuth desktop client. This file will be your **`oauth_client_credentials.json`**.

---

### A couple more commands

`list [-l]`
    To simply list all IAPs in your project. `-l` To print all local prices, instead of only default prices.

`restore [-v] [-l]`
    To reset all local prices to the default prices. `-v` To see IAPs lists during restoring, `-l` to also see local prices

---

### Creating new IAPs from a csv

The Play Console has no bulk way to add products, and clicking twenty of them in by hand is exactly
as fun as it sounds. So there is a small round trip instead: export what you already have, add rows
to it in a spreadsheet, send it back.

    dotnet run -- export-iaps

writes every One-time product into the csv at `ProductDefinitionsFilePath` from `config.json`
(`./product-definitions.csv` next to it by default):

    product_id,default_price,title,description
    crystals_1,0.99,Tiny of Gems,Tiny of Gems
    crystals_6,99.99,Vault of Gems,Vault of Gems

The price column is the price in your `DefaultRegion` only. All the other regions are left out on
purpose, they are what `localize` computes from the percentage template.

The title and the description are the store listing in `DefaultLanguageCode` (`en-US` by default).
The other languages are not exported, and a created product gets that one listing only, the rest are
added in the Play Console.

Add a row per new product, then:

    dotnet run -- create-iaps -n     # dry run, shows what would be created
    dotnet run -- create-iaps        # for real
    dotnet run -- localize           # apply the percentage template to them

Products that already exist are skipped and **never** modified, so running `create-iaps` twice is
safe and the file can stay as a full snapshot of your catalog. Existing prices are only ever touched
by `restore` and `localize`.

All new products go to Google in **one batch request**, the way Google recommends for catalog
creation (`allowMissing` + `LATENCY_TOLERANT`). Prices for every region come from Google's own
exchange rates, and the region list is copied from a product you already have, so a new product
lands in exactly the same countries as the rest of your catalog. The tax category is not set, the
products get the Play Console default.

One row per product. Only the backward compatible purchase option is exported, and a created product
gets exactly one. The csv separator is detected automatically, so re-exporting the file from Numbers
or Excel as `;`-separated works too.

---

### Android vitals export

The Play Console shows crashes, ANRs, slow starts and the rest of Android vitals spread over dozens of screens,
with the actually interesting slices (this device model, that API level, that one release) hidden behind filters.

    dotnet run vitals

exports all of it into **one markdown file** you can drop into ChatGPT/Claude and just ask *"what is wrong with my app?"*.

The report contains:

- daily (or hourly) timelines for crash rate, ANR rate and error report counts - including the `userPerceived*`
  variants Google uses for the "bad behaviour" thresholds (2% crashes / 0.47% ANRs)
- breakdowns by `versionCode`, `apiLevel`, `deviceModel` and `countryCode`, ranked by real impact
  (`rate × affected users`), so a 40% crash rate on 3 devices does not drown out a regression on your whole install base
- the top crash/ANR clusters with **full sample stack traces**, affected version/API ranges, links to the Console
  and Google's own hints about the issue
- anomalies Google detected on its own
- data freshness per metric set, so you know how much of the tail is still missing

Useful variants:

    dotnet run vitals --days 56 --sets all --by all --top 25     # everything, wide
    dotnet run vitals --period HOURLY --days 3 --sets crash,anr  # right after a release
    dotnet run vitals --filter "versionCode = 1234" --issues 50  # one release, deep
    dotnet run vitals --samples 0                                # numbers only, no stack traces
    dotnet run vitals --max-trace-lines 0                        # full ANR thread dumps
    dotnet run vitals --format both                              # markdown + raw json

By default the window is the **last 28 days**, ending at the freshest data each metric set actually has
(they are not equally fresh, so a section can end a day earlier than the rest - the report says so).
**No version filter is applied** - all releases in the `OS_PUBLIC` cohort are included, and `versionCode`
shows up as one of the breakdowns. Use `--filter "versionCode = 1234"` to narrow it down.

Sample traces are trimmed to the first 150 lines, because an ANR report is a dump of *every* thread
in the process and the blocked main thread is at the top.

Run `dotnet run vitals --help` for the full option list.

Reports are written to `VitalsOutputPath` from `config.json` (`./vitals-export` next to it by default),
or to whatever you pass in `--out`.

**Two things to set up first**, because this uses a *different* Google API than the IAP commands:

1. enable **`Google Play Developer Reporting API`** in your [Google Cloud Console](https://console.cloud.google.com/apis/library/playdeveloperreporting.googleapis.com)
   for the same project your OAuth client belongs to
2. the first `vitals` run asks for consent again - it needs the `playdeveloperreporting` scope.
   That token is cached separately, so your IAP commands keep working without re-consenting.

Your Play Console account needs at least the *"View app information (read-only)"* permission for the app.

---

### Examples

1. You cloned the repository and built the program.
1. You open the command line in the project folder.
1. You downloaded and placed the `client_credentials.json` file with the `client secrets` in the folder next to the project folder.
1. you created and placed the `config.json` file in the folder next to the project folder.
1. you created and placed the `default-prices-in-local-currency.json` file in the folder next to the project folder.
1. The app for which you want to localize prices has the package name `com.MyApp.Package`.

your config.json

    {
        "PackageName": "com.MyApp.Package",
        "CredentialsFilePath": "./oauth_client_credentials.json",
        "DefaultPricesFilePath": "./default-prices.json",
        "DefaultCurrency": "USD"
    }

your default-prices.json

    {
        "crystals_1": 10,
        "crystals_6": 50,
    }

You opened Project in visual studio code and opened terminal.
Then, your commands will likely look like this:

To see the list of all IAPs:

    dotnet run -- list -l

To reset local prices to the default prices:

    dotnet run -- restore

To localize the prices:

    dotnet run -- localize

To export all IAPs into a csv, and create the new ones you added to it:

    dotnet run -- export-iaps
    dotnet run -- create-iaps -n
    dotnet run -- create-iaps

_first extra `--` its delimiter for `dotnet run` so you can pass any parameters and they all will be passe to our program instead of `dotnet run` command._


