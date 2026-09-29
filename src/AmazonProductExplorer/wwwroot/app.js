(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const terminalStatuses = new Set(["completed", "partial", "failed", "cancelled", "interrupted"]);
  const knownStatuses = new Set(["queued", "searching", "enriching", ...terminalStatuses]);
  const storageKey = "amazon-product-explorer.last-job";
  const availabilityOptions = ["In stock", "Limited stock", "Out of stock", "Unknown"];
  const svgNamespace = "http://www.w3.org/2000/svg";
  const state = {
    config: null,
    job: null,
    busy: false,
    sequence: 0,
    timer: null,
    pollingStarted: 0,
    pollingErrors: 0,
    sort: "rank",
    sortDirection: 1,
    lastAnnouncement: "",
    lastId: readStoredId()
  };

  function readStoredId() {
    try {
      const id = localStorage.getItem(storageKey);
      return validJobId(id) ? id : null;
    } catch {
      return null;
    }
  }

  function validJobId(value) {
    return typeof value === "string" && /^[a-zA-Z0-9_-]{1,100}$/.test(value);
  }

  function rememberJob(id) {
    state.lastId = id;
    try { localStorage.setItem(storageKey, id); } catch { /* Storage is optional. */ }
  }

  function text(tag, value, className) {
    const element = document.createElement(tag);
    element.textContent = String(value ?? "");
    if (className) element.className = className;
    return element;
  }

  function svgElement(tag, attributes = {}, value) {
    const element = document.createElementNS(svgNamespace, tag);
    for (const [key, attribute] of Object.entries(attributes)) element.setAttribute(key, String(attribute));
    if (value !== undefined) element.textContent = String(value);
    return element;
  }

  function finiteNumber(value) {
    return typeof value === "number" && Number.isFinite(value);
  }

  function validPrice(product) {
    return finiteNumber(product.price) && product.price >= 0;
  }

  function validRating(product) {
    return finiteNumber(product.rating) && product.rating >= 0 && product.rating <= 5;
  }

  function currencyCode(product) {
    return typeof product.currency === "string" && /^[A-Z]{3}$/.test(product.currency) ? product.currency : null;
  }

  function formatMoney(value, currency = "USD", maximumFractionDigits = 2) {
    if (!finiteNumber(value)) return "Not available";
    if (!currency) return `${value.toFixed(2)} (currency unknown)`;
    try {
      return new Intl.NumberFormat("en-US", {
        style: "currency", currency, minimumFractionDigits: 0, maximumFractionDigits
      }).format(value);
    } catch {
      return `${currency} ${value.toFixed(maximumFractionDigits)}`;
    }
  }

  function formatCount(value) {
    return finiteNumber(value) ? new Intl.NumberFormat("en-US").format(value) : "—";
  }

  function dateLabel(value) {
    const date = new Date(value);
    if (!value || Number.isNaN(date.getTime())) return "Collection time unavailable";
    return date.toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
  }

  function availability(product) {
    return availabilityOptions.includes(product.availability) ? product.availability : "Unknown";
  }

  function stockClass(value) {
    return value.toLowerCase().replaceAll(" ", "-");
  }

  function selectedMode() {
    return document.querySelector('input[name="mode"]:checked').value;
  }

  function updateMode() {
    const live = selectedMode() === "live";
    const note = $("mode-note");
    note.replaceChildren();
    if (live) {
      note.append(text("span", "LIVE", "demo-pill"), document.createTextNode("Uses paid Oxylabs requests: search pages plus up to one detail request per product, before retries."));
    } else {
      note.append(text("span", "DEMO", "demo-pill"), document.createTextNode("Synthetic example data. No API credentials or paid requests needed."));
    }
    $("live-warning").hidden = !live || state.config?.liveConfigured === true;
    if (live && !state.config) {
      $("live-warning").textContent = "Live setup could not be verified. Check that the server is running and reload this page. Demo mode remains available.";
    } else {
      $("live-warning").textContent = "Live scraping is not configured. Add your Oxylabs credentials on the server using the setup guide, then restart the application.";
    }
    $("search-button").disabled = state.busy || (live && state.config?.liveConfigured !== true);
  }

  function setBusy(busy) {
    state.busy = busy;
    for (const control of document.querySelectorAll('#search-form input, #search-form select, input[name="mode"]')) control.disabled = busy;
    $("search-button").querySelector("span").textContent = busy ? "Collecting products…" : "Explore products";
    updateMode();
  }

  function announce(message) {
    if (state.lastAnnouncement === message) return;
    state.lastAnnouncement = message;
    $("announcement").textContent = message;
  }

  function showError(message, reconnect = false) {
    $("error-message").textContent = message;
    $("error-banner").hidden = false;
    $("retry-button").hidden = !reconnect;
  }

  function clearError() {
    $("error-banner").hidden = true;
    $("retry-button").hidden = true;
  }

  async function requestJson(url, options = {}) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 20000);
    try {
      const response = await fetch(url, { ...options, signal: controller.signal, headers: { Accept: "application/json", ...options.headers } });
      let payload = null;
      if (response.headers.get("content-type")?.includes("json")) {
        try { payload = await response.json(); } catch { /* Fall through to the response check. */ }
      }
      if (!response.ok) {
        const description = payload?.error || payload?.detail || payload?.message || payload?.title;
        const fallback = response.status === 404
          ? "This job could not be found. It may have been removed; start a new search."
          : `The server returned an error (${response.status}). Please try again.`;
        const error = new Error(typeof description === "string" ? description.slice(0, 1000) : fallback);
        error.status = response.status;
        throw error;
      }
      if (response.status === 204) return null;
      if (payload === null) throw new Error("The server returned an unexpected response. Check that the application is running correctly.");
      return payload;
    } catch (error) {
      if (error.name === "AbortError") throw new Error("The server took too long to respond. Check your connection and try again.");
      if (error instanceof TypeError) throw new Error("Cannot reach the application server. Check that it is running, then try again.");
      throw error;
    } finally {
      clearTimeout(timeout);
    }
  }

  function stopPolling() {
    clearTimeout(state.timer);
    state.timer = null;
  }

  function isValidJob(job) {
    return job && validJobId(job.id) && knownStatuses.has(job.status) && Array.isArray(job.products);
  }

  function schedulePoll(id, sequence, delay = 2000) {
    stopPolling();
    state.timer = setTimeout(() => pollJob(id, sequence), delay);
  }

  async function pollJob(id, sequence) {
    if (sequence !== state.sequence) return;
    if (Date.now() - state.pollingStarted > 30 * 60 * 1000) {
      pausePolling("This job has been running for more than 30 minutes. Automatic updates have stopped; reconnect to check its status.");
      return;
    }
    try {
      const job = await requestJson(`/api/jobs/${encodeURIComponent(id)}`);
      if (sequence !== state.sequence) return;
      if (!isValidJob(job)) throw new Error("The server returned an invalid job response.");
      state.pollingErrors = 0;
      clearError();
      applyJob(job);
      if (!terminalStatuses.has(job.status)) schedulePoll(id, sequence);
    } catch (error) {
      if (sequence !== state.sequence) return;
      state.pollingErrors += 1;
      if (error.status === 404 || state.pollingErrors >= 3) {
        pausePolling(error.message, error.status !== 404);
      } else {
        showError(`Connection interrupted. Retrying (${state.pollingErrors}/3)… ${error.message}`);
        schedulePoll(id, sequence, state.pollingErrors * 3000);
      }
    }
  }

  function pausePolling(message, reconnect = true) {
    stopPolling();
    setBusy(false);
    $("progress-label").textContent = "Updates paused";
    $("cancel-button").hidden = !state.job || terminalStatuses.has(state.job.status);
    showError(message, reconnect);
  }

  async function startSearch(event) {
    event.preventDefault();
    if (state.busy || !$("search-form").reportValidity()) return;
    const query = $("query").value.trim();
    if (query.length < 2) {
      showError("Enter at least two characters for your product search.");
      $("query").focus();
      return;
    }
    const mode = selectedMode();
    if (mode === "live" && state.config?.liveConfigured !== true) return;
    const limit = Number($("limit").value);
    const zipCode = $("zip-code").value;
    stopPolling();
    clearError();
    const sequence = ++state.sequence;
    state.job = null;
    state.pollingErrors = 0;
    state.sort = "rank";
    state.sortDirection = 1;
    $("table-filter").value = "";
    $("resume-banner").hidden = true;
    $("results").hidden = true;
    $("empty-state").hidden = true;
    $("job-panel").hidden = false;
    $("job-status").textContent = "Submitting";
    $("job-status").dataset.status = "queued";
    $("job-title").textContent = `Exploring “${query}”`;
    $("job-message").textContent = "Creating your research job…";
    $("warnings-panel").hidden = true;
    $("progress-wrap").hidden = false;
    $("job-progress").removeAttribute("value");
    $("progress-label").textContent = "Waiting to start";
    $("cancel-button").hidden = true;
    setBusy(true);
    announce("Starting product search.");
    try {
      const accepted = await requestJson("/api/jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ query, limit, zipCode, mode })
      });
      if (sequence !== state.sequence) return;
      if (!validJobId(accepted?.id)) throw new Error("The server did not return a valid job ID.");
      rememberJob(accepted.id);
      state.pollingStarted = Date.now();
      await pollJob(accepted.id, sequence);
    } catch (error) {
      if (sequence !== state.sequence) return;
      setBusy(false);
      $("job-status").textContent = "Could not start";
      $("job-status").dataset.status = "failed";
      $("job-message").textContent = "Your search was not confirmed. You can try again.";
      $("progress-wrap").hidden = true;
      showError(error.message);
    }
  }

  async function loadPrevious() {
    const id = state.job?.id || state.lastId;
    if (!validJobId(id)) return;
    stopPolling();
    clearError();
    $("resume-banner").hidden = true;
    state.pollingStarted = Date.now();
    state.pollingErrors = 0;
    const sequence = ++state.sequence;
    setBusy(true);
    await pollJob(id, sequence);
  }

  async function cancelJob() {
    const job = state.job;
    if (!job || terminalStatuses.has(job.status)) return;
    const sequence = state.sequence;
    $("cancel-button").disabled = true;
    $("cancel-button").textContent = "Cancelling…";
    try {
      await requestJson(`/api/jobs/${encodeURIComponent(job.id)}/cancel`, { method: "POST" });
      if (sequence !== state.sequence) return;
      stopPolling();
      state.pollingStarted = Date.now();
      await pollJob(job.id, sequence);
    } catch (error) {
      if (sequence === state.sequence) showError(`Could not confirm cancellation. ${error.message}`, true);
    } finally {
      if (sequence === state.sequence) {
        $("cancel-button").disabled = false;
        $("cancel-button").textContent = "Cancel search";
      }
    }
  }

  function applyJob(job) {
    state.job = job;
    rememberJob(job.id);
    const done = terminalStatuses.has(job.status);
    const products = job.products.filter((product) => product && typeof product === "object");
    job.products = products;
    setBusy(!done);
    if (done) stopPolling();
    $("empty-state").hidden = true;
    $("job-panel").hidden = false;
    $("job-status").textContent = job.status.charAt(0).toUpperCase() + job.status.slice(1);
    $("job-status").dataset.status = job.status;
    $("job-title").textContent = `Results for “${job.query || "your search"}”`;
    $("job-message").textContent = job.message || (done ? "Collection has finished." : "Collecting product data…");
    $("cancel-button").hidden = done;
    $("cancel-button").disabled = false;
    $("cancel-button").textContent = "Cancel search";
    $("progress-wrap").hidden = done;
    const completed = Number.isInteger(job.completedDetails) ? job.completedDetails : 0;
    const failed = Number.isInteger(job.failedDetails) ? job.failedDetails : 0;
    if (job.status === "enriching" && products.length) {
      $("job-progress").value = Math.min(100, ((completed + failed) / products.length) * 100);
      $("progress-label").textContent = `${completed}/${products.length} details${failed ? ` · ${failed} failed` : ""}`;
    } else {
      $("job-progress").removeAttribute("value");
      $("progress-label").textContent = job.status === "searching" ? `${products.length} products found` : "Waiting to start";
    }
    const warnings = Array.isArray(job.warnings) ? job.warnings.filter((warning) => typeof warning === "string") : [];
    $("warnings-panel").hidden = warnings.length === 0;
    $("warnings-list").replaceChildren(...warnings.map((warning) => text("li", warning)));
    $("results").hidden = products.length === 0 && !done;
    if (!$("results").hidden) renderResults();
    announce(`${job.status}. ${products.length} products found. ${completed} details collected.${failed ? ` ${failed} failed.` : ""}`);
  }

  function renderResults() {
    const job = state.job;
    const products = job.products;
    const demo = job.mode === "demo";
    const pricesByCurrency = new Map();
    for (const product of products) {
      const currency = currencyCode(product);
      if (!validPrice(product) || !currency) continue;
      pricesByCurrency.set(currency, (pricesByCurrency.get(currency) || 0) + 1);
    }
    const currency = pricesByCurrency.has("USD") ? "USD" : [...pricesByCurrency].sort((a, b) => b[1] - a[1])[0]?.[0] || "USD";
    const priced = products.filter((product) => validPrice(product) && currencyCode(product) === currency);
    const rated = products.filter(validRating);
    const sortedPrices = priced.map((product) => product.price).sort((a, b) => a - b);
    const middle = Math.floor(sortedPrices.length / 2);
    const median = sortedPrices.length ? (sortedPrices[middle] + sortedPrices[Math.floor((sortedPrices.length - 1) / 2)]) / 2 : null;
    const counts = Object.fromEntries(availabilityOptions.map((value) => [value, products.filter((product) => availability(product) === value).length]));
    $("results-title").textContent = "Your product overview";
    $("results-context").textContent = `${job.query} · Delivery ZIP ${job.zipCode} · Updated ${dateLabel(job.updatedAt || job.createdAt)}`;
    $("data-source-label").textContent = demo ? "SYNTHETIC DEMO DATA" : "LIVE COLLECTION";
    $("data-source-label").classList.toggle("live", !demo);
    $("demo-disclosure").hidden = !demo;
    $("metric-count").textContent = formatCount(products.length);
    $("metric-count-note").textContent = `${formatCount(job.limit)} requested · ${products.filter((product) => product.detailStatus === "complete").length} details complete`;
    $("metric-price").textContent = median === null ? "—" : formatMoney(median, currency);
    $("metric-price-note").textContent = `${priced.length} ${currency} prices · ${products.length - priced.length} missing / other currency`;
    $("metric-rating").textContent = rated.length ? (rated.reduce((sum, product) => sum + product.rating, 0) / rated.length).toFixed(2) : "—";
    $("metric-rating-note").textContent = `${rated.length} rated · ${products.length - rated.length} unrated · unweighted`;
    $("metric-stock").textContent = formatCount(counts["Limited stock"]);
    $("metric-stock-note").textContent = `${counts["Unknown"]} with unknown availability`;
    $("histogram-caption").textContent = `${currency} · ${priced.length} of ${products.length} products with usable prices`;
    const comparable = priced.filter(validRating);
    $("scatter-caption").textContent = `${currency} · ${comparable.length} products with both price and rating`;
    renderHistogram(priced, currency);
    renderScatter(comparable, currency);
    $("availability-summary").replaceChildren(...availabilityOptions.map((value) => {
      const item = text("span", "");
      const dot = document.createElement("i");
      dot.className = stockClass(value);
      dot.setAttribute("aria-hidden", "true");
      item.append(dot, document.createTextNode(`${value} `), text("b", counts[value]));
      return item;
    }));
    $("export-link").href = `/api/jobs/${encodeURIComponent(job.id)}/export`;
    $("export-link").setAttribute("aria-label", `Export all ${products.length} collected products as CSV`);
    $("table-note").textContent = pricesByCurrency.size > 1
      ? "Price sorting groups currencies. No currency conversion is applied."
      : "Select a column heading to sort. CSV exports all collected products.";
    renderTable();
  }

  function safeAmazonUrl(value) {
    if (typeof value !== "string" || value.length > 2000) return null;
    try {
      const url = new URL(value);
      if (url.protocol !== "https:" || !["amazon.com", "www.amazon.com"].includes(url.hostname.toLowerCase()) || url.username || url.password || url.port) return null;
      return url.href;
    } catch { return null; }
  }

  function compareProducts(a, b) {
    const key = state.sort;
    if (key === "price" || key === "rating" || key === "rank") {
      const check = key === "price" ? validPrice : key === "rating" ? validRating : (product) => finiteNumber(product.rank);
      const aValid = check(a), bValid = check(b);
      if (!aValid && bValid) return 1;
      if (aValid && !bValid) return -1;
      if (!aValid && !bValid) return 0;
      if (key === "price" && currencyCode(a) !== currencyCode(b)) return (currencyCode(a) || "ZZZ").localeCompare(currencyCode(b) || "ZZZ");
      return (a[key] - b[key]) * state.sortDirection;
    }
    const aValue = key === "availability" ? availability(a) : String(a.title || "");
    const bValue = key === "availability" ? availability(b) : String(b.title || "");
    return aValue.localeCompare(bValue) * state.sortDirection;
  }

  function renderTable() {
    if (!state.job) return;
    const filter = $("table-filter").value.toLocaleLowerCase().trim();
    const products = state.job.products.filter((product) => `${product.title || ""} ${product.asin || ""} ${availability(product)} ${product.availabilityText || ""}`.toLocaleLowerCase().includes(filter)).sort(compareProducts);
    const rows = products.map((product) => {
      const row = document.createElement("tr");
      const title = typeof product.title === "string" && product.title ? product.title : "Title unavailable";
      row.append(text("td", finiteNumber(product.rank) ? String(product.rank).padStart(2, "0") : "—", "rank-cell"));
      const titleCell = text("td", "", "title-cell");
      const titleText = text("span", title, "product-title");
      titleText.title = title;
      titleCell.append(titleText);
      if (product.detailStatus === "pending") titleCell.append(text("p", "Fetching product details…", "product-detail-state"));
      if (product.detailStatus === "failed") titleCell.append(text("p", "Detail fetch failed; some fields may be missing", "product-detail-state"));
      if (state.job.mode !== "demo" && typeof product.detailNote === "string" && product.detailNote) titleCell.append(text("p", product.detailNote, "product-detail-state"));
      row.append(titleCell);
      row.append(text("td", validPrice(product) ? formatMoney(product.price, currencyCode(product)) : "Not available", validPrice(product) ? "price-cell" : "no-value"));
      const ratingCell = document.createElement("td");
      if (validRating(product)) {
        const rating = text("span", "", "rating-main");
        const star = text("span", "★", "rating-star");
        star.setAttribute("aria-hidden", "true");
        rating.append(star, document.createTextNode(product.rating.toFixed(1)), text("span", " / 5", "rating-denominator"));
        ratingCell.append(rating);
      } else ratingCell.append(text("span", "Not available", "no-value"));
      if (finiteNumber(product.reviewCount) && product.reviewCount >= 0) ratingCell.append(text("span", `${formatCount(product.reviewCount)} reviews`, "review-count"));
      row.append(ratingCell);
      const stockCell = document.createElement("td");
      const status = availability(product);
      stockCell.append(text("span", status, `stock-pill ${stockClass(status)}`));
      const stockMessage = typeof product.availabilityText === "string" && product.availabilityText
        ? product.availabilityText
        : Number.isInteger(product.limitedStock) && product.limitedStock >= 0 ? `${product.limitedStock} reported remaining` : "";
      if (stockMessage && stockMessage.toLowerCase() !== status.toLowerCase()) stockCell.append(text("span", stockMessage, "stock-message"));
      row.append(stockCell, text("td", product.asin || "Not available", "asin-cell"));
      const linkCell = document.createElement("td");
      const url = state.job.mode === "demo" ? null : safeAmazonUrl(product.url);
      if (url) {
        const link = text("a", "↗", "product-link");
        link.href = url;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
        link.setAttribute("aria-label", `View ${title} on Amazon (opens in a new tab)`);
        link.title = "View on Amazon";
        linkCell.append(link);
      } else linkCell.append(text("span", state.job.mode === "demo" ? "Demo" : "No link", "source-unavailable"));
      row.append(linkCell);
      return row;
    });
    if (!rows.length) {
      const row = document.createElement("tr");
      const cell = text("td", filter ? "No collected products match your filter." : "No products were collected. Try a different search or check the collection notes.", "no-results");
      cell.colSpan = 7;
      row.append(cell);
      rows.push(row);
    }
    $("products-body").replaceChildren(...rows);
    $("table-count").textContent = String(state.job.products.length);
    $("table-footer-count").textContent = `Showing ${products.length} of ${state.job.products.length} collected products`;
    for (const button of document.querySelectorAll("[data-sort]")) {
      const selected = button.dataset.sort === state.sort;
      button.closest("th").setAttribute("aria-sort", selected ? state.sortDirection === 1 ? "ascending" : "descending" : "none");
      button.querySelector("span").textContent = selected ? state.sortDirection === 1 ? "↑" : "↓" : "↕";
    }
  }

  function chartBase(id, title, description) {
    const chart = svgElement("svg", { viewBox: "0 0 500 236", role: "img", "aria-labelledby": `${id}-title ${id}-description` });
    chart.append(svgElement("title", { id: `${id}-title` }, title), svgElement("desc", { id: `${id}-description` }, description));
    return chart;
  }

  function emptyChart(container, description) {
    container.replaceChildren(text("p", description, "chart-empty"));
  }

  function renderHistogram(products, currency) {
    const container = $("price-chart");
    if (!products.length) {
      emptyChart(container, "A price distribution appears when product prices are available.");
      return;
    }
    const prices = products.map((product) => product.price);
    const minimum = Math.min(...prices), maximum = Math.max(...prices);
    const binCount = minimum === maximum ? 1 : Math.min(6, Math.max(2, Math.ceil(Math.sqrt(prices.length))));
    const step = (maximum - minimum) / binCount;
    const bins = Array.from({ length: binCount }, (_, index) => ({ low: minimum + index * step, high: minimum + (index + 1) * step, count: 0 }));
    prices.forEach((price) => { bins[step ? Math.min(binCount - 1, Math.floor((price - minimum) / step)) : 0].count += 1; });
    const maxCount = Math.max(...bins.map((bin) => bin.count));
    const tickStep = Math.max(1, Math.ceil(maxCount / 4));
    const yMaximum = Math.ceil(maxCount / tickStep) * tickStep;
    const chart = chartBase("price-distribution", `Price distribution in ${currency}`, `${products.length} products. ${bins.map((bin) => `${formatMoney(bin.low, currency)} to ${formatMoney(bin.high, currency)}: ${bin.count}`).join(". ")}.`);
    const left = 37, top = 27, width = 442, height = 159, base = top + height;
    for (let count = 0; count <= yMaximum; count += tickStep) {
      const y = base - count / yMaximum * height;
      chart.append(svgElement("line", { x1: left, y1: y, x2: left + width, y2: y, class: "gridline" }));
      chart.append(svgElement("text", { x: left - 10, y: y + 3, "text-anchor": "end" }, count));
    }
    chart.append(svgElement("text", { x: left, y: 14, class: "axis-title" }, "Products"));
    const binWidth = width / binCount;
    bins.forEach((bin, index) => {
      const gap = binCount === 1 ? 140 : 13;
      const barHeight = bin.count / yMaximum * height;
      const label = binCount === 1 ? `${formatMoney(bin.low, currency)}: ${bin.count} products` : `${formatMoney(bin.low, currency)} to ${index === binCount - 1 ? "and including " : "below "}${formatMoney(bin.high, currency)}: ${bin.count} products`;
      const rect = svgElement("rect", { x: left + index * binWidth + gap / 2, y: base - barHeight, width: Math.max(8, binWidth - gap), height: barHeight, rx: 4, class: "bar", tabindex: "0", "aria-label": label });
      rect.append(svgElement("title", {}, label));
      chart.append(rect);
      chart.append(svgElement("text", { x: left + index * binWidth + binWidth / 2, y: base - barHeight - 7, "text-anchor": "middle" }, bin.count));
    });
    if (binCount === 1) chart.append(svgElement("text", { x: left + width / 2, y: base + 19, "text-anchor": "middle" }, formatMoney(minimum, currency)));
    else {
      for (let index = 0; index <= binCount; index += 1) chart.append(svgElement("text", { x: left + index * binWidth, y: base + 19, "text-anchor": "middle" }, formatMoney(minimum + index * step, currency, maximum < 10 ? 2 : 0)));
    }
    chart.append(svgElement("text", { x: left + width / 2, y: 230, "text-anchor": "middle", class: "axis-title" }, `Product price (${currency})`));
    container.replaceChildren(chart);
  }

  function renderScatter(products, currency) {
    const container = $("rating-chart");
    if (!products.length) {
      emptyChart(container, "This view needs products with both a price and a rating.");
      return;
    }
    const rawMaximum = Math.max(...products.map((product) => product.price));
    const maximum = Math.max(1, rawMaximum * 1.08);
    const chart = chartBase("price-rating", `Price versus rating in ${currency}`, `${products.length} products. Horizontal axis: product price from zero to ${formatMoney(maximum, currency)}. Vertical axis: rating from zero to five. Each point is one listing; points can overlap.`);
    const left = 37, top = 27, width = 442, height = 159, base = top + height;
    for (let rating = 0; rating <= 5; rating += 1) {
      const y = base - rating / 5 * height;
      chart.append(svgElement("line", { x1: left, y1: y, x2: left + width, y2: y, class: "gridline" }));
      chart.append(svgElement("text", { x: left - 10, y: y + 3, "text-anchor": "end" }, rating));
    }
    chart.append(svgElement("text", { x: left, y: 14, class: "axis-title" }, "Rating / 5"));
    for (let index = 0; index <= 4; index += 1) chart.append(svgElement("text", { x: left + index / 4 * width, y: base + 19, "text-anchor": "middle" }, formatMoney(index / 4 * maximum, currency, maximum < 10 ? 2 : 0)));
    products.forEach((product) => {
      const label = `${product.title || product.asin || "Product"}: ${formatMoney(product.price, currency)}, ${product.rating.toFixed(1)} out of 5`;
      const point = svgElement("circle", { cx: left + product.price / maximum * width, cy: base - product.rating / 5 * height, r: 5, class: "chart-dot", tabindex: "0", "aria-label": label });
      point.append(svgElement("title", {}, label));
      chart.append(point);
    });
    chart.append(svgElement("text", { x: left + width / 2, y: 230, "text-anchor": "middle", class: "axis-title" }, `Product price (${currency})`));
    container.replaceChildren(chart);
  }

  $("search-form").addEventListener("submit", startSearch);
  document.querySelectorAll('input[name="mode"]').forEach((radio) => radio.addEventListener("change", updateMode));
  $("cancel-button").addEventListener("click", cancelJob);
  $("resume-button").addEventListener("click", loadPrevious);
  $("retry-button").addEventListener("click", loadPrevious);
  $("table-filter").addEventListener("input", renderTable);
  document.querySelectorAll("[data-sort]").forEach((button) => button.addEventListener("click", () => {
    state.sortDirection = state.sort === button.dataset.sort ? -state.sortDirection : 1;
    state.sort = button.dataset.sort;
    renderTable();
  }));
  $("resume-banner").hidden = !state.lastId;
  requestJson("/api/config").then((config) => {
    state.config = config;
    updateMode();
  }).catch(() => {
    state.config = null;
    updateMode();
  });
})();
