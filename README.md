# ADS 讀變數：四種寫法差多少

同一批 PLC 變數，C# 用四種 ADS 讀法跑一遍，數字並排：

| 模式 | 寫法 | 量什麼 |
|---|---|---|
| A 普通寫法 | 迴圈 1000 次，每次 `ReadAny` 一個 `MAIN.aValues[i]` | 讀完 1000 個的耗時、ADS 請求次數 |
| B Sum Read | 1000 個 handle 打包成 ADS Sum command 一次讀回 | 同上 |
| C 輪詢 | 每 10 ms 讀一次 `MAIN.stTrigger` | 每秒 ADS 請求數、PLC 改值 → C# 看到的延遲、程序 CPU% |
| D Notification | `AddDeviceNotification`，OnChange | 同上 |

這是教學短片「TwinCAT 小妙招 #1」的示範程式。

> **狀態**：PLC Build 0 error、`dotnet build` 0 error 0 warning。**還沒在 runtime 上跑過**，下面「預期量級」都是初判，實測後更新。

## 目錄

1. [需求環境](#需求環境)
2. [專案結構](#專案結構)
3. [PLC 端](#plc-端)
4. [怎麼跑](#怎麼跑)
5. [四種模式各量什麼](#四種模式各量什麼)
6. [為什麼差這麼多](#為什麼差這麼多)
7. [預期量級（初判，未實測）](#預期量級初判未實測)
8. [注意事項](#注意事項)
9. [參考資料](#參考資料beckhoff-infosys)
10. [English summary](#english-summary)

## 需求環境

| 項目 | 內容 |
|---|---|
| TwinCAT | TwinCAT 3.1 Build 4026（XAE + Runtime）；專案用 XAE 4026.22 建立 |
| 授權 | 只用基本 PLC（TC1200），不需要任何 TF 授權。UmRT / XAR 用 7 天 trial 即可 |
| PLC library | `Tc2_Standard`、`Tc2_System`、`Tc3_Module`（新專案預設就有） |
| Runtime | 本機 UmRT（User Mode Runtime）或 Windows XAR 都可以 |
| .NET | .NET 8 SDK；NuGet `Beckhoff.TwinCAT.Ads` **7.0.389**（版本寫死在 csproj） |

## 專案結構

```
plc/
  AdsReadDemo.sln
  AdsReadDemo/
    AdsReadDemo.tsproj               PlcTask 10 ms
    AdsReadDemoPlc/
      POUs/MAIN.TcPOU                aValues 每 cycle 更新、stTrigger 定時 +1
      DUTs/ST_Trigger.TcDUT          nTrigger + 改值時的 PLC 時間戳 (12 bytes)
      GVLs/GVL_Param.TcGVL           陣列長度、相位、預設觸發間隔
csharp/
  AdsReadDemo.csproj
  Program.cs                         連線、依 --mode 跑、印對照表
  BulkRead.cs                        模式 A / B
  ChangeWatch.cs                     模式 C / D、延遲量測、CPU%
  PlcSymbols.cs                      PLC symbol 路徑 (只寫在這裡)
  Options.cs                         命令列參數與預設值 (只寫在這裡)
  Strings.cs                         中英文字
  Report.cs                          終端機對照表
```

## PLC 端

`MAIN`（PlcTask，10 ms）：

| 變數 | 型別 | 用途 |
|---|---|---|
| `aValues` | `ARRAY[1..1000] OF LREAL` | 每 cycle 更新：`aValues[i] = i + SIN(相位 + i × 0.01)`，C# 每次讀到的值都不一樣 |
| `stTrigger.nTrigger` | `UDINT` | 每 `tTriggerPeriod`（預設 2 s）+1 |
| `stTrigger.nPlcTime` | `ULINT` | 改值當下的 `F_GetSystemTime()`（100 ns，從 1601-01-01 UTC 起算） |
| `tTriggerPeriod` | `TIME` | 自動 +1 的間隔，可從 ADS 改 |
| `bAutoTrigger` | `BOOL` | FALSE 時停止自動 +1（`--trigger host` 會暫時關掉） |
| `nHostRequest` | `UDINT` | C# 寫入不同的值，下一個 cycle `nTrigger` +1 |
| `nPlcTimeNow` | `ULINT` | 每 cycle 的 `F_GetSystemTime()`，C# 檢查 PLC 與 Windows 時鐘差用 |
| `nCycleCount` | `UDINT` | 程式執行次數，確認 task 真的在跑 |

`ST_Trigger` 用 `{attribute 'pack_mode' := '1'}`，`nTrigger` 與 `nPlcTime` 同一個 cycle 寫入，C# 一次讀 12 bytes，兩個欄位不會錯開。

## 怎麼跑

### 1. PLC：Build → Activate → Run

1. 用 TwinCAT XAE 開 `plc/AdsReadDemo.sln`。
2. 選目標（本機 UmRT 或 XAR），Solution Platform 跟著目標選（UmRT 是 `TwinCAT OS (x64)`、Windows XAR 是 `TwinCAT RT (x64)`）。
3. Build → Activate Configuration → Login → Start。

### 2. C# console

```powershell
cd csharp
dotnet run -c Release                                   # 跑全部 (A → B → C → D)，最後印對照表
dotnet run -c Release -- --mode a                        # 只跑一種
dotnet run -c Release -- --netid 5.1.2.3.1.1             # UmRT 有自己的 AmsNetId 時
dotnet run -c Release -- --trigger host --seconds 20     # 延遲只用 C# 端時鐘量 (UmRT 建議)
dotnet run -c Release -- --lang en                       # 英文輸出
dotnet run -c Release -- --help
```

| 參數 | 預設 | 說明 |
|---|---|---|
| `--mode <a\|b\|c\|d\|all>` | `all` | 跑哪個模式 |
| `--netid <AmsNetId>` | `AmsNetId.Local`（127.0.0.1.1.1） | 目標 |
| `--port <n>` | 851 | PLC ADS port |
| `--count <n>` | 1000 | A / B 讀幾個元素（1..1000） |
| `--repeat <n>` | 5 | A / B 各跑幾輪，取平均與最快 |
| `--sum-chunk <n>` | 500 | 一個 Sum command 最多帶幾個子命令（InfoSys 建議上限，見[下面](#為什麼差這麼多)） |
| `--seconds <s>` | 10 | C / D 各跑幾秒 |
| `--poll-ms <ms>` | 10 | C 的輪詢間隔 |
| `--notify-cycle-ms <ms>` | 10 | D 的 `NotificationSettings` CycleTime |
| `--notify-max-delay-ms <ms>` | 0 | D 的 `NotificationSettings` MaxDelay |
| `--trigger <plc\|host>` | `plc` | 誰讓 `nTrigger` +1、延遲怎麼算（見[延遲怎麼量](#延遲怎麼量)） |
| `--host-trigger-ms <ms>` | 2000 | `--trigger host` 時 C# 觸發的間隔 |
| `--max-clock-drift-ms <ms>` | 20 | `--trigger plc` 時容許的 PLC / Windows 時鐘差 |
| `--lang <zh\|en>` | `zh` | 輸出語言 |

## 四種模式各量什麼

### A 普通寫法：一次讀一個

```csharp
for (int i = 0; i < handles.Length; i++)
    values[i] = (double)client.ReadAny(handles[i], typeof(double));   // 1 個值 = 1 次 ADS 請求
```

- 量：讀完 1000 個的耗時（`--repeat` 輪的平均與最快）、ADS 請求次數（= 1000）。
- 1000 個 variable handle 事先用 Sum command 建好，不算進計時。這已經是「逐一讀」裡比較省的做法；每次用 symbol 名稱讀（`ReadValue("MAIN.aValues[1]", ...)`）可能還要多一次取 handle 的請求（初判，未量）。

### B Sum Read：一次請求讀一批

```csharp
var cmd = new SumHandleReadAnyType(client, handles, types, SumFallbackMode.Sum);
object[] values = cmd.Read().Values;                                    // 500 個值 = 1 次 ADS 請求
```

- 同一組 handle，打包成 ADS Sum command（IndexGroup `0xF080`）。預設每 500 個一包，1000 個 = 2 次請求；`--sum-chunk 1000` 就是 1 次。
- `SumFallbackMode.Sum`：目標不支援 Sum command 時直接丟例外，不會偷偷退回逐一讀；讀完再檢查 `ExecutedAsSum`，確保請求次數是真的。

### C 輪詢：每 10 ms 問一次

- 每 `--poll-ms` 讀一次 `MAIN.stTrigger`（12 bytes），跑 `--seconds` 秒。
- Windows 預設 timer 解析度約 15.6 ms，`Thread.Sleep(10)` 實際會睡更久；輪詢期間用 `timeBeginPeriod(1)` 調成 1 ms，結束還原。
- 量：ADS 請求數與每秒請求數、看到幾次變化、延遲（平均 / 最大）、這個程序的 CPU%（`Process.TotalProcessorTime`，換算成整台電腦的 %，同工作管理員）。

### D Notification：值變了 PLC 才送

```csharp
var settings = new NotificationSettings(AdsTransMode.OnChange, cycleTime: 10, maxDelay: 0);
uint h = client.AddDeviceNotification("MAIN.stTrigger", 12, settings, null);
client.AdsNotification += (s, e) => { /* e.Data = 12 bytes */ };
```

- C# 只送兩次請求（Add + Delete），之後 PLC 端在值變時主動送通知。
- 量：同模式 C；另外列「PLC 通知」數（PLC 送來的訊息數）。註冊後的第一個通知是當下的值，只當基準，不算變化。

### 延遲怎麼量

| `--trigger` | 誰觸發 | 延遲 = | 前提 |
|---|---|---|---|
| `plc`（預設） | PLC 每 `tTriggerPeriod` 自動 +1 | C# 收到時的 Windows UTC 時間 − `stTrigger.nPlcTime` | PLC 與 Windows 時鐘一致。程式在每個模式前後讀 `nPlcTimeNow` 檢查時鐘差，超出 −1 ~ `--max-clock-drift-ms` 就不給延遲數字 |
| `host` | C# 每 `--host-trigger-ms` 寫 `MAIN.nHostRequest` | 看到變化的 Stopwatch − 寫入前的 Stopwatch | 不依賴 PLC 時鐘；包含一次 ADS 寫入與 PLC 下一個 cycle，所以是上限 |

`plc` 模式的 `nPlcTime` 在 cycle 開頭取得，所以延遲包含「cycle 開頭到 PLC 真的寫完」的那一小段。

## 為什麼差這麼多

**A vs B：差在請求次數，不是資料量。** 1000 個 LREAL 只有 8 KB；A 慢在 1000 次「送請求 → 等 PLC 回」的來回，每次都有 ADS router 轉送與 PLC 端處理的固定成本。Sum command 把 N 個子請求（IndexGroup / IndexOffset / Length）塞進一個請求，PLC 一次處理完一次回覆 [1][2]。

但 Sum command 不是越大越好：PLC 會把整個請求處理完才開始下一個 cycle，子命令太多會讓 PLC 的 cycle 抖動；InfoSys 建議一個 Sum command 不超過 500 個子命令 [1]，子命令太多會讓 PLC 執行抖動、回覆也要放得進 AMS router 的記憶體（預設 2048 KB）[3]。所以預設 `--sum-chunk 500`。

**C vs D：差在誰去問。** 輪詢是 C# 每 10 ms 問一次「變了沒」，值 2 秒才變一次，99% 的請求都白問；10 秒 ≈ 1000 次請求。Notification 是 C# 註冊一次，PLC 端每 `CycleTime` 比對一次值，變了才送 [5][6][8]；10 秒只有 Add + Delete 兩次請求、加上幾次通知。

代價在 PLC 端：OnChange 的比對由 runtime 做，數量多會加重即時系統的負擔；通知數量有限，不用的要刪掉 [4]；舊版文件寫的上限是每個 ADS port 550 個、server 端註冊 1024 個 [6][7]（TwinCAT 2 文件，TC3 是否相同為初判）。所以「每個變數一個 notification」不適合大量變數；大量變數改用結構或 Sum command 一次註冊（初判，本專案沒示範）。

**延遲：初判兩者同一個量級。** 兩邊都被「多久看一次」限制：輪詢是 `--poll-ms`，notification 是 `CycleTime`，預設都是 10 ms，再加上 PLC task cycle。Notification 主要贏在請求數和 CPU，不一定贏在延遲；想看延遲差異，可以把 `--notify-cycle-ms` 調小或把 `--poll-ms` 調大再比。

## 預期量級（初判，未實測）

以下是推估，**不是量測結果**，實跑後以實測取代。

| 模式 | 耗時 / 延遲 | ADS 請求 | 依據 |
|---|---|---|---|
| A | 本機 XAR 約 100 ms ~ 1 s；UmRT 可能到秒級 | 1000 | 每次本機 ADS 來回約 0.1 ~ 1 ms（初判） |
| B | 約 1 ~ 10 ms | 2（`--sum-chunk 1000` 時 1） | 請求次數少 500 倍，資料量相同 |
| C | 平均延遲約 5 ~ 15 ms，最大約 10 ~ 30 ms | 10 s 約 900 ~ 1000 次（~100 次/s） | 平均約半個輪詢間隔 + PLC cycle |
| D | 延遲與 C 同量級 | 2（Add + Delete），通知約 = 變化次數 + 1 | 見上一節 |

CPU%：C 與 D 都很低（整台電腦的 1% 以下，初判），差距在多核心機器上可能看不出來；用 `--poll-ms 1` 放大差異會比較明顯。

## 注意事項

- **UmRT 不是 real-time**：PLC task 實際跑的次數可能只有設定值的幾分之一，`tTriggerPeriod` 2 s 在牆鐘上可能更久；10 秒內看到的變化可能很少，跑 C / D 時建議加 `--seconds 30` 或用 `--trigger host`。數字只代表當下那台機器，不代表實機。
- **UmRT 的 PLC 時間可能不跟 Windows 時鐘走**：`--trigger plc` 會檢查時鐘差，超出就不給延遲；這時改用 `--trigger host`。
- **UmRT 的 AmsNetId**：UmRT 若有自己的 AmsNetId（不是本機系統的 NetId），要用 `--netid` 指定，否則常見 ADS error 6 / 7。
- **Notification 數量**：本程式只註冊 1 個，跑完就刪；Ctrl+C 中斷時 `AdsClient.Dispose` 會關連線（初判：連線關閉後 router 會清掉該連線的 notification）。
- **`--trigger host` 會寫 PLC 變數**：`bAutoTrigger`（跑完還原成原本的值）與 `nHostRequest`。只對示範 PLC 用。
- A / B 的耗時包含 .NET 端的 marshal；B 每次都重建 Sum command 物件（本機計算，量級遠小於一次 ADS 來回，初判）。

## 參考資料（Beckhoff InfoSys）

1. [ADS-sum command: Read or Write a list of variables with one single ADS-command](https://infosys.beckhoff.com/content/1033/tc3_adsdll2/124835083.html) — IndexGroup 0xF080、子命令格式、建議上限 500、PLC 處理完整個請求才進下一個 cycle
2. [ADS-Sum Command: Reading or writing several variables (.NET 範例)](https://infosys.beckhoff.com/content/1033/tc3_adssamples_net/185258507.html) — 用 `ReadWrite(0xF080, ...)` 手動組 Sum command；本專案改用 `TwinCAT.Ads.SumCommand` 的現成類別
3. [ISumCommand Interface](https://infosys.beckhoff.com/content/1033/tcadsnetref/7313686667.html)（TwinCAT 2 ADS.NET 文件）— 子命令太多造成 PLC jitter、回覆大小受 router 記憶體限制
4. [Use of ADS Notifications (.NET)](https://infosys.beckhoff.com/content/1033/tc3_adsnetref/7312578699.html) — notification 數量有限，不用的要刪
5. [NotificationSettings Properties](https://infosys.beckhoff.com/content/1033/tc3_adsnetref/7313318027.html) — CycleTime、MaxDelay、NotificationMode；預設 OnChange / 200 ms / MaxDelay off
6. [AdsTransMode Enumeration](https://infosys.beckhoff.com/content/1033/tcadsnetref/7313078411.html)（TwinCAT 2 ADS.NET 文件）— OnChange 由 runtime 比對值、server 端註冊上限 1024
7. [AdsSyncAddDeviceNotificationReq](https://infosys.beckhoff.com/content/1033/tcadsdll2/12444732299.html)（TwinCAT 2 ADS-DLL 文件）— 每個 ADS port 550 個 notification
8. [IAdsNotifications.AddDeviceNotificationEx](https://infosys.beckhoff.com/content/1033/tc3_adsnetref/7313217675.html) — cycleTime / maxDelay 參數意義

## License

MIT，見 [LICENSE](LICENSE)。

## English summary

A demo for the short video "TwinCAT tip #1": the same PLC variables read four ways over ADS from a C# console (.NET 8, `Beckhoff.TwinCAT.Ads` 7.0.389), with the numbers printed side by side.

| Mode | How | Measures |
|---|---|---|
| A plain | loop 1000 times, `ReadAny` one `MAIN.aValues[i]` each | time to read 1000 values, ADS request count |
| B Sum Read | 1000 handles packed into ADS Sum commands (`SumHandleReadAnyType`, 500 per command by default) | same |
| C polling | read `MAIN.stTrigger` every 10 ms | requests per second, PLC-change → C#-sees-it latency, process CPU% |
| D notification | `AddDeviceNotification`, OnChange | same |

- **PLC** (`plc/`): basic PLC only, no TF license. `aValues : ARRAY[1..1000] OF LREAL` changes every cycle; `stTrigger.nTrigger` increments every `tTriggerPeriod` (2 s) and `stTrigger.nPlcTime` stores `F_GetSystemTime()` at that moment.
- **Run**: activate the PLC on UmRT or XAR, then `cd csharp; dotnet run -c Release` (all modes plus a comparison table). `--netid` for a UmRT with its own AmsNetId, `--lang en` for English output, `--help` for all options.
- **Latency**: `--trigger plc` (default) compares the Windows UTC time with the PLC timestamp and refuses to report a number when the PLC and Windows clocks disagree; `--trigger host` lets C# trigger the change and measures with its own Stopwatch only (recommended on UmRT, whose PLC time may not follow the wall clock).
- **Why the gap**: A pays one round trip per value, B one per 500 values (InfoSys recommends at most 500 sub-commands so the PLC cycle does not jitter). C asks every 10 ms although the value changes every 2 s; D registers once and the PLC pushes only on change. Notifications cost PLC-side comparisons and are limited in number (older TwinCAT 2 docs: 550 per ADS port, 1024 server-side registrations; whether TC3 is the same is an estimate), so delete unused ones. Latency of C and D is expected to be of the same order (both bounded by 10 ms check intervals) — estimate, not measured.
- **Status**: PLC builds with 0 errors and the C# project with 0 warnings; **not yet run on a runtime**. All expected numbers above are estimates.
