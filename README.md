# SerialDataLogger

A Windows desktop application that collects, stores, and monitors measurement data from serial/TCP instruments in real time.

It runs as a single executable with no installer. The communication, parsing, storage, and UI layers are separate, so adapting it to a different instrument means replacing the parser — not rewriting the application.

The repository also includes **DeviceSimulator**, a separate app that emits three channels of test data and can inject malformed lines on demand. It exists so the failure paths can be exercised without physical hardware.

---

## Screens

| Tab | What it does |
|---|---|
| Live log | Streams incoming data as it arrives; parsed values and parse failures are shown distinctly |
| Data table | Query by time range and channel, inspect the raw text of failed lines, export to Excel |
| Chart | Per-channel trend plot with zoom, pan, and range lock |
| Alarms | Per-channel high/low limits, plus a history of alarm and recovery events |

---

## Stack

| | |
|---|---|
| Language / framework | C# / .NET 10 / WinForms |
| Database | SQLite (Microsoft.Data.Sqlite) |
| Transport | TCP socket (structured so `SerialPort` drops in) |
| Charting | ScottPlot |
| Excel | ClosedXML |
| Version control | Git / GitHub |

The UI is built entirely in code — no designer files.

---

## Layout

```
SerialDataLogger/          Collector (main application)
├─ Form1.cs                UI and screen flow
├─ Reading.cs              Measurement model
├─ ReadingParser.cs        Parsing of received strings
├─ Database.cs             SQLite writes and queries
├─ ChartBuffer.cs          Sliding buffer for the chart
├─ AlarmMonitor.cs         Threshold evaluation (state-based)
├─ Threshold.cs            Threshold and alarm models
└─ ExcelExporter.cs        xlsx export

DeviceSimulator/           Instrument simulator (testing and demo)
└─ Form1.cs                Sends 3 channels, injects malformed data
```

**Layer separation.** Communication, parsing, storage, and presentation are each isolated. A different instrument means a new parser; a different platform means a new UI. Nothing else moves.

---

## Wire format

```
$DATA,T1,23.5,C,2026-08-27T13:20:06
 ─┬── ─┬─ ─┬── ┬  ────────┬────────
  │    │   │   │          └─ timestamp
  │    │   │   └─ unit
  │    │   └─ value
  │    └─ channel
  └─ start marker
```

## Schema

```sql
CREATE TABLE readings (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    ts       TEXT NOT NULL,
    channel  TEXT NOT NULL,
    value    REAL NOT NULL,
    unit     TEXT,
    raw      TEXT              -- original received text, kept for diagnosis
);

CREATE TABLE thresholds (
    channel  TEXT PRIMARY KEY,
    lo       REAL,             -- NULL = no low limit check
    hi       REAL,             -- NULL = no high limit check
    enabled  INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE alarms (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    ts       TEXT NOT NULL,
    channel  TEXT NOT NULL,
    value    REAL NOT NULL,
    kind     TEXT NOT NULL,    -- 'HI' / 'LO'
    limit_v  REAL NOT NULL     -- the limit in force when the alarm fired
);
```

---

## Design decisions

The assumption throughout is unattended operation over long periods. These are the decisions that assumption forced.

### 1. Bad data does not stop collection

Corrupted lines are normal, not exceptional — noise on the line, firmware differences, unstable power. Parsing is `TryParse`-based rather than exception-based, so a malformed line does not break the collection loop.

Lines that fail to parse are still written to the database, with the original text preserved in the `raw` column. Without that text you cannot tell after the fact whether the cause was the cable, the instrument's configuration, or a failing sensor.

### 2. Locale-independent number handling

Number and date parsing pin `InvariantCulture` explicitly. When the decimal separator is interpreted differently, nothing throws — the values are simply wrong, which is the kind of fault that survives for months before anyone notices.

### 3. Stream framing

TCP and serial streams have no message boundaries. Incoming bytes are accumulated in a buffer and split on the delimiter (`\r\n`), so a packet that arrives cut in half does not corrupt a reading.

### 4. Receiving is separate from writing

The receive thread only appends to an in-memory buffer. A separate timer flushes that buffer to SQLite once per second inside a single transaction. Compared to per-row inserts this cuts disk sync count sharply, and — more importantly — a slow disk cannot block the socket.

### 5. Thread safety

The receive thread and the flush timer touch the same collection, so it is guarded by a `lock` — but the critical section covers only the collection swap. The database write itself happens outside the lock.

UI access is marshalled to the UI thread via `InvokeRequired`, so background threads never touch controls directly.

### 6. Every buffer has a ceiling

Anything that could grow without bound during a long run is capped:

- Log text: last 500 lines
- Chart data: last 300 points per channel (sliding window)
- Query results: 5,000 rows maximum

### 7. Alarms fire on state change, not per value

An alarm is recorded once when a channel enters an out-of-range state and once when it returns to normal. A channel that stays out of range does not generate a growing pile of identical alarms.

Each alarm row also stores the limit that was in force at the time. If the threshold is edited later, the historical record still explains why the alarm fired.

### 8. Where the database file lives

The database is created under `%LOCALAPPDATA%`, not next to the executable. This avoids both the working-directory problem (shortcuts and Task Scheduler resolve it differently) and the write-permission problem when the app sits under Program Files.

The full database path is shown in the status bar and clicking it opens the containing folder — during remote support the user can locate the file without being talked through it.

### 9. Excel export preserves types

Dates and numbers are written as dates and numbers rather than strings, so sorting, filtering, and charting work in Excel immediately. The raw-text column is forced to text format for the opposite reason: to stop Excel from silently reinterpreting the original data.

Export uses the rows already on screen instead of re-querying the database. Otherwise data collected between the query and the export would appear in the file but not in the view, and the two row counts would disagree.

### 10. Deployment

Published as a self-contained single file. No .NET runtime installation — copy one executable and run it. This is aimed at sites where internet access is blocked or installing software requires approval.

---

## Running it

### Binary

Run `SerialDataLogger.exe`, click **Connect**. No installation, no runtime prerequisites. Windows 10 or later, x64.

### From source

```
git clone https://github.com/chapse57/SerialDataLogger.git
```

Open `SerialDataLogger.slnx` in Visual Studio 2022 or later and build.

To see it work, start `DeviceSimulator` first and click **Start sending**, then click **Connect** in `SerialDataLogger`.

---

## Roadmap

| Item | Scope of work |
|---|---|
| Real serial port | ~30 lines in the connection layer. Parsing, storage, and UI unchanged |
| Modbus RTU/TCP | Separate parser plus polling logic |
| Multiple simultaneous devices | A connection management layer |
| Threshold hysteresis | Prevents alarm chatter when a value oscillates around a limit |
| Data retention policy | Automatic cleanup or monthly partitioning of aged data |
