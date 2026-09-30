# Build and run Amazon Product Explorer

This guide takes you from a new computer to a local dashboard that searches Amazon.com through Oxylabs. Start with **Demo** mode: its products are synthetic and it makes no paid API calls. Move to **Live** mode after configuring your own Oxylabs account.

## 1. Understand the pieces

| Piece | Meaning in this project |
| --- | --- |
| C# | The language used for the server and data processing. |
| .NET SDK | The tools that compile and run C# code. Install the SDK, not only a runtime. |
| ASP.NET Core | The .NET framework that serves the website and its API. |
| Frontend | The form, table and charts running in your browser. This project uses HTML, CSS and JavaScript. |
| Backend | The C# application running on your computer. It holds credentials and communicates with Oxylabs. |
| API | An interface through which programs send requests and receive structured responses. |
| Oxylabs Web Scraper API | The service that fetches Amazon pages and returns parsed product data. |
| ASIN | Amazon's product identifier. It helps remove duplicate listings; variants may have different ASINs. |
| Git / GitHub | Git records changes locally; GitHub stores and shares the repository online. |
| CrewAI | An optional future Python service for written analysis. It is not installed or implemented in this version. |

You do not need Node.js, npm, Python, CrewAI or a separate database to run this version.

## 2. Install the development tools

1. Download the **.NET 10 SDK** from the [official .NET downloads page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
2. On an Apple Silicon Mac, choose **macOS Arm64**. On an Intel Mac, choose **macOS x64**. On Windows, choose the SDK installer matching your processor. Microsoft provides [macOS installation instructions](https://learn.microsoft.com/en-us/dotnet/core/install/macos) and [Windows installation instructions](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
3. Complete the installer and reopen Terminal on macOS, or PowerShell on Windows.
4. Verify the installation:

```sh
dotnet --list-sdks
dotnet --info
git --version
```

The SDK list must include a version beginning with `10.`. If Git is missing, install it before cloning the project. On macOS, running `git --version` may offer Apple's command-line developer tools.

An editor is optional for running the app. To change the code, install Visual Studio Code and its Microsoft **C# Dev Kit** extension, then open the repository folder.

## 3. Open your project

If you already have the project in the workspace created for this task, use that folder:

```sh
cd "/Users/thibakarsri/Documents/ChatGPT/Web Scraping"
```

On another computer, or when starting a fresh copy, clone the public repository instead:

```sh
git clone https://github.com/ThibakarSri/amazon-product-explorer.git
cd amazon-product-explorer
```

Use one route, not both. The remaining commands assume your terminal is in the repository root, the folder containing `README.md` and `src`.

## 4. Compile and start the platform

In the original macOS workspace, a project-local SDK is already installed. You can start immediately with:

```sh
./scripts/run.sh
```

For other .NET commands in this workspace, use `./scripts/dotnet.sh` wherever this guide says `dotnet`. A fresh clone does not include the local SDK; install the SDK as described above.

Run these commands one at a time:

```sh
dotnet restore src/AmazonProductExplorer/AmazonProductExplorer.csproj
dotnet build src/AmazonProductExplorer/AmazonProductExplorer.csproj
dotnet run --project src/AmazonProductExplorer
```

`restore` prepares project dependencies, `build` compiles the code, and `run` starts the web server. Keep the terminal open while using the app.

Open [http://localhost:5080](http://localhost:5080) in your browser. The address is on your own computer. The repository being public does not publish a running website.

The normal development launch profile selects the Development environment so .NET can load your user secrets later. Use the plain `dotnet run --project src/AmazonProductExplorer` command in this guide.

To stop the server, return to its terminal and press **Control+C**. To restart, run the last command again.

## 5. Complete your first search in Demo mode

1. Keep **Demo** selected.
2. Enter a keyword, for example `wireless headphones`.
3. Choose **10 products** for the first run.
4. Keep the US delivery ZIP code `10001` or enter another valid five-digit US ZIP code.
5. Start the search and wait for progress to finish.
6. Inspect the results table, dashboard and CSV export.

Demo results are invented examples that exercise the interface and data handling. They are not current Amazon listings, prices or stock information. Changing the keyword in Demo mode does not contact Amazon.

Check that you can identify the title, price, rating out of five, availability message, product link and ASIN. Missing values should remain clearly missing. Sorting the results changes their display order; it does not change which products were originally selected.

## 6. Set up your Oxylabs account

1. Open the [Oxylabs dashboard](https://dashboard.oxylabs.io/) and create or sign into your account.
2. Enable **Web Scraper API** access for your account. Review the account's current usage limits and pricing in the dashboard before making live requests.
3. Create or obtain the Web Scraper API **username and password**. These are API credentials and may differ from your dashboard login.
4. Keep those values available locally for the next step. You do not need to send them to an assistant or add them to GitHub.

Oxylabs' [getting-started guide](https://developers.oxylabs.io/get-started/quick-start-web-scraper-api) explains account setup and API credentials. Dashboard labels can change; look for Web Scraper API access rather than a residential-proxy product.

## 7. Save credentials outside the repository

Stop the application with Control+C. On macOS/Linux, the easiest option is the interactive helper:

```sh
./scripts/setup-oxylabs.sh
```

It asks for your API username and password locally, hides password input, and saves them in .NET user secrets. Input is not added to shell history. Restart with `./scripts/run.sh` afterward.

Alternatively, including on Windows, use these commands from the repository root:

```sh
dotnet user-secrets --project src/AmazonProductExplorer set 'Oxylabs:Username' 'YOUR_API_USERNAME'
dotnet user-secrets --project src/AmazonProductExplorer set 'Oxylabs:Password' 'YOUR_API_PASSWORD'
```

Replace the placeholders with your own credentials on your computer, keeping the single quotes around each value. If a value itself contains an apostrophe, use your terminal's escaping rules instead of pasting it unchanged into those quotes. These commands can appear in shell history, so avoid sharing terminal recordings or command-history output containing your credentials.

The project already includes its user-secrets identifier; you do not need `dotnet user-secrets init`. The values are stored outside the source folder. Microsoft describes user secrets as a development convenience: they are **not encrypted** and are not a production secret store. See [ASP.NET Core user-secrets documentation](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0).

Restart the server:

```sh
dotnet run --project src/AmazonProductExplorer
```

Keep credentials out of `appsettings.json`, JavaScript, screenshots, issues and commits. If a credential is accidentally published, revoke or rotate it in Oxylabs; removing it from the latest file does not remove it from Git history.

## 8. Run a small live search

Before starting a paid search, check your saved credentials with this free account request:

```sh
dotnet run --project src/AmazonProductExplorer -- --check-oxylabs
```

This command loads the same Development configuration as the app, contacts Oxylabs' [free usage endpoint](https://developers.oxylabs.io/products/web-scraper-api/usage-and-billing/usage-statistics), and exits without opening a web server or submitting a scrape. It prints no passwords or account statistics. Success confirms access to that endpoint; it does not guarantee remaining credits or Amazon scraping permissions.

If it reports **HTTP 401**, open the Oxylabs dashboard's **Web Scraper API** API-user credentials. Copy the API username and its matching password, or reset that API user's password if needed. Run `./scripts/setup-oxylabs.sh` to replace both saved values, and repeat the check. Your dashboard email/password, proxy credentials and API tokens are different from the Basic-auth API user credentials this app expects. Do not post credentials or `dotnet user-secrets list` output in screenshots or issues.

If it reports **HTTP 403**, check the account's access to Web Scraper API and the requested resource with Oxylabs. Changing a password is not necessarily the remedy for 403. See [Oxylabs' status-code meanings](https://developers.oxylabs.io/products/web-scraper-api/response-codes).

Run these commands from the clone you actually use; the project path is relative to your current directory. After updating credentials, stop the old server with **Control+C** and start it again. The **Live Amazon** toggle checks only whether credential values are present, not whether Oxylabs accepts them. Old failed search results remain unchanged; submit a new search after fixing credentials.

1. Refresh the browser and select **Live** mode.
2. Search for `wireless headphones` with a target of **10 products** and ZIP `10001`.
3. Watch the progress while the server finds unique products and retrieves their details.
4. Check the results against a few linked Amazon pages using the same delivery location. Prices and availability can change between visits.
5. Check Oxylabs usage in your dashboard.
6. Once the small search works and the usage is acceptable, repeat with a target of **60**.

Live mode retrieves search pages and then individual product pages. A 60-product search can involve multiple search-page requests plus up to 60 product-detail requests. Retries and the provider's billing rules can affect usage; the app's target count is not a price quotation.

“Top 60” means **up to the first 60 unique, non-sponsored listings returned in Amazon's default search order** for the keyword and location. It does not mean the 60 best-selling or highest-rated products. A sparse search or retrieval failures can produce fewer rows.

The parser prefers an eligible new, one-time buy-box offer and keeps its price and availability together. It excludes offers labelled used, renewed, refurbished, subscription or rental. When no buy-box array is present, it uses the page's top-level price and stock fields. See [the architecture guide](ARCHITECTURE.md) for mapping details.

An availability message such as “Only 3 left” is a reported message for that offer at collection time. It is not a verified count of all Amazon inventory. Missing price, rating or stock information remains unknown; it is not replaced with zero or an out-of-stock assertion.

## 9. Understand the data and charts

The table is the detailed record. The charts summarize the records collected in that search. Missing prices and ratings are excluded from the corresponding numerical calculations, so the number of usable values can be smaller than the number of products.

The dashboard is a snapshot. A price-versus-rating chart is not proof of product quality, and a single search cannot establish price trends or sales volume. Review count provides context for ratings.

The application saves job snapshots as local JSON files under the app's `App_Data` folder. This folder is ignored by Git. Keep the application's storage if you want to retain results. This version uses local file storage rather than a database or a distributed queue; see [the architecture guide](ARCHITECTURE.md) for restart and deployment limitations.

The non-secret settings in `src/AmazonProductExplorer/appsettings.json` initially allow at most **6 search pages**, **3 concurrent detail requests**, polling every **3 seconds**, and **180 seconds per provider job**. A search containing many product jobs can take longer than 180 seconds overall. Leave these settings unchanged for your first run.

## 10. Save future changes on GitHub

GitHub stores the source, documentation and change history. Keep API credentials and collected data on your computer unless you deliberately decide to publish a sanitized example.

For each change, review it, stage the specific files you intend to publish, commit, then push:

```sh
git status
git diff
git add docs/SETUP.md
git diff --cached
git commit -m "Improve setup instructions"
git push
```

In this example only the setup guide is staged. Substitute the actual filenames you edited. If Git requests authentication, sign in using your GitHub tooling; do not put a token in a remote URL or source file.

## 11. Where CrewAI fits later

Complete the live data workflow first. C# maps and validates product records, and the browser calculates dashboard summaries from them; no AI agent is needed for either step.

A later CrewAI integration could be a separate Python service that receives sanitized records and precomputed metrics, generates a short comparison, and returns it to the C# API. Its output should cite the collected products, distinguish missing data, and avoid inventing prices or stock. It would require its own Python environment, model configuration and costs. See the [official CrewAI introduction](https://docs.crewai.com/en/introduction).

**There is no CrewAI service in this repository yet.** Nothing in the current setup requires installing it.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| `dotnet: command not found` | Install the .NET 10 SDK, reopen the terminal and rerun `dotnet --info`. Check the official OS installation guide if it is still absent from your PATH. |
| Build says the target framework is unsupported | Confirm `dotnet --list-sdks` contains .NET 10 and that you are running the expected `dotnet` installation. |
| Project or file cannot be found | Move to the repository root before running the commands. Quote the workspace path because it contains spaces. |
| Browser cannot reach localhost | Keep the server running and read its “Now listening on” address. Check the terminal for startup errors. |
| Port 5080 is already in use | Stop the other copy of this app. Alternatively run `dotnet run --project src/AmazonProductExplorer -- --urls http://localhost:5081` and visit port 5081. |
| Live mode says credentials are missing | Set both secrets for this project, restart the app and use its Development launch profile. Do not add the credentials to the browser. |
| Oxylabs returns 401 | Authentication was rejected. Replace both Web Scraper API user credentials, run the free `--check-oxylabs` check, then restart the server. |
| Oxylabs returns 403 | The account cannot access the requested resource. Check Web Scraper API access/account status with Oxylabs. |
| Provider reports a usage or account limit | Check your account credits, plan and access with Oxylabs. Repeatedly submitting the same search will not fix an account limitation. |
| Live work times out or finishes partly | Inspect the job message and completed rows. A temporary provider or network failure can occur. Resolve the cause before submitting another paid search. |
| Availability is Unknown | The response did not contain a recognized availability message. The app deliberately avoids inferring stock from a price. |
| Price is missing / provider reports `no_price` | Some listings do not expose a normal buy-box price. Inspect the product page; missing price should stay missing. |
| There are fewer than 60 products | Search pages may contain ads, duplicate ASINs or fewer organic results; requests may also fail or reach the configured page limit. |
| GitHub has code but no running dashboard | A repository hosts your source. You still run the app locally or deploy its C# server to an appropriate host. GitHub Pages does not run an ASP.NET Core backend. |

## When the first version is complete

- Demo mode runs without credentials and visibly identifies its synthetic data.
- A small Live search completes with your account and shows the requested fields.
- You have checked sample rows and reviewed API usage before requesting 60.
- Missing fields and partial results are visible rather than silently invented.
- Your public repository contains source and instructions, with no credentials or local result files.

For a public hosted service, first add authentication, per-user quotas, durable database/queue storage, a production secret manager and HTTPS. The current app is a local portfolio prototype bound to your computer by default.
