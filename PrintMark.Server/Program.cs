using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PrintMark.Core;
using PrintMark.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<PrintMark.Server.Services.PrinterDiscoveryService>();
builder.Services.AddSingleton<AuditDatabase>(_ => new AuditDatabase("printmark_enterprise_audit.db"));
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

string? apiKey = Environment.GetEnvironmentVariable("PRINTMARK_API_KEY");
var database = app.Services.GetRequiredService<AuditDatabase>();
var discovery = app.Services.GetRequiredService<PrintMark.Server.Services.PrinterDiscoveryService>();

string policyFile = Path.Combine(AppContext.BaseDirectory, "watermark_policy.json");
object policyLock = new();

WatermarkConfig LoadWatermarkPolicy()
{
    lock (policyLock)
    {
        if (File.Exists(policyFile))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<WatermarkConfig>(File.ReadAllText(policyFile));
                if (cfg != null) return cfg;
            }
            catch { }
        }
        return new WatermarkConfig();
    }
}

void SaveWatermarkPolicy(WatermarkConfig config)
{
    lock (policyLock)
    {
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(policyFile, json);
    }
}

bool KeyOk(HttpRequest req)
{
    if (string.IsNullOrEmpty(apiKey)) return true;
    string given = req.Headers["X-Api-Key"].ToString();
    if (string.IsNullOrEmpty(given)) given = req.Headers["X-API-KEY"].ToString();
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(apiKey));
}

// 1. Watermark Policy Endpoints (Used by Web UI, AdminApp, and Client Daemons)
app.MapGet("/api/watermark/config", () => Results.Ok(LoadWatermarkPolicy()));

app.MapPost("/api/watermark/config", ([FromBody] WatermarkConfig config) =>
{
    SaveWatermarkPolicy(config);
    return Results.Ok(new { status = "updated", config });
});

// 2. Fleet Auto-Discovery Endpoints
app.MapGet("/api/fleet/printers", async ([FromQuery] string? subnet) =>
{
    var (detectedSubnet, printers) = await discovery.DiscoverNetworkPrintersAsync(subnet);
    return Results.Ok(new
    {
        subnet = detectedSubnet,
        count = printers.Count,
        printers = printers
    });
});

app.MapGet("/api/fleet/subnet", () => Results.Ok(new
{
    detectedSubnet = PrintMark.Server.Services.PrinterDiscoveryService.DetectActiveSubnet()
}));

// 3. Audit Log & Telemetry Endpoints
app.MapPost("/api/audit/log", ([FromBody] PrintAuditRecord record, HttpRequest req) =>
{
    if (!KeyOk(req)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(record.ShortCode)) return Results.BadRequest(new { error = "ShortCode is required" });

    try
    {
        bool inserted = database.LogPrintJob(record);
        return Results.Ok(new { status = inserted ? "success" : "duplicate", jobId = record.JobId, code = record.ShortCode });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/api/audit/log", ([FromQuery] int? take) => Results.Ok(database.GetRecent(take ?? 50)));

app.MapGet("/api/audit/stats", () =>
{
    var s = database.GetStats();
    return Results.Ok(new
    {
        totalJobsProcessed = s.TotalJobs,
        jobsLast24Hours = s.Last24Hours,
        distinctUsers = s.DistinctUsers,
        distinctWorkstations = s.DistinctComputers,
        totalPages = s.TotalPages,
        serverStatus = "Healthy"
    });
});

app.MapGet("/api/audit/verify/{code}", (string code) =>
{
    var r = database.FindByShortCode(code.Trim());
    if (r is null) return Results.NotFound(new { found = false });

    string raw = $"{r.KeyId}|{r.JobId}|{r.UserName}|{r.ComputerName}|{r.ClientIp}|{r.Timestamp:O}";
    bool valid = StampCodes.ComputeTag(raw).Equals(r.HmacTag, StringComparison.OrdinalIgnoreCase);
    return Results.Ok(new { found = true, hmacValid = valid, record = r });
});

app.MapGet("/", () => Results.Content(Dashboard.Html, "text/html; charset=utf-8"));

app.Run("http://0.0.0.0:5000");

static class Dashboard
{
    public const string Html = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>PrintMark Enterprise Audit & Watermark Studio</title>
<style>
  :root{--bg:#0f172a;--card:#1e293b;--ink:#f8fafc;--mute:#94a3b8;--line:#334155;--accent:#0284c7;--red:#dc2626}
  *{box-sizing:border-box} body{margin:0;font:14px/1.45 system-ui,Segoe UI,sans-serif;background:var(--bg);color:var(--ink)}
  header{padding:16px 24px;border-bottom:1px solid var(--line);display:flex;justify-content:space-between;align-items:center;background:#0b1120}
  h1{font-size:19px;margin:0} h1 b{color:#38bdf8} .live{color:#22c55e;font-size:12px;font-weight:600}
  main{padding:20px 24px;max-width:1280px;margin:auto}
  .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin-bottom:18px}
  .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px}
  .card .n{font-size:26px;font-weight:700;color:#38bdf8} .card .l{color:var(--mute);font-size:12px}
  .grid2{display:grid;grid-template-columns:1.2fr 1fr;gap:16px;margin-bottom:18px}
  @media(max-width:900px){.grid2{grid-template-columns:1fr}}
  .box{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px;margin-bottom:18px;overflow:auto}
  .box h2{margin:0 0 12px 0;font-size:15px;color:#38bdf8;text-transform:uppercase;letter-spacing:.5px}
  .form-row{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-bottom:10px}
  label{display:block;font-size:12px;color:var(--mute);margin-bottom:4px}
  input,select{width:100%;padding:8px 10px;border:1px solid var(--line);border-radius:6px;background:#0f172a;color:var(--ink)}
  button{padding:9px 16px;border:0;border-radius:6px;background:var(--accent);color:#fff;font-weight:600;cursor:pointer}
  button.preset{background:#334155;font-size:12px;padding:6px 10px;margin-right:6px;margin-bottom:6px}
  button.preset:hover{background:#475569}
  table{width:100%;border-collapse:collapse;min-width:760px} th,td{padding:9px 12px;text-align:left;border-bottom:1px solid var(--line);white-space:nowrap}
  th{color:#38bdf8;font-weight:600;font-size:12px;background:#0f172a} tr:last-child td{border-bottom:0}
  code{font-family:ui-monospace,Consolas,monospace;font-size:12px;color:#cbd5e1}
  .ok{color:#22c55e;font-weight:600} .bad{color:#ef4444;font-weight:600}
</style></head><body>
<header><h1><b>PrintMark</b> Enterprise Audit & Watermark Studio</h1><span class="live" id="live">● Live Connected</span></header>
<main>
  <div class="cards">
    <div class="card"><div class="n" id="s_total">–</div><div class="l">Total Jobs Stamped</div></div>
    <div class="card"><div class="n" id="s_24">–</div><div class="l">Last 24 Hours</div></div>
    <div class="card"><div class="n" id="s_users">–</div><div class="l">Distinct Users</div></div>
    <div class="card"><div class="n" id="s_pcs">–</div><div class="l">Workstations</div></div>
    <div class="card"><div class="n" id="s_pages">–</div><div class="l">Pages Printed</div></div>
  </div>

  <div class="grid2">
    <!-- WATERMARK DESIGN STUDIO -->
    <div class="box">
      <h2>Watermark Design Studio (Live Fleet Policy)</h2>
      <div style="margin-bottom:10px">
        <label>Quick Disclaimers & Presets:</label>
        <button class="preset" onclick="setPreset('CONFIDENTIAL',0,'#DC2626')">CONFIDENTIAL (Diagonal)</button>
        <button class="preset" onclick="setPreset('CONFIDENTIAL',1,'#64748B')">CONFIDENTIAL (Center)</button>
        <button class="preset" onclick="setPreset('DO NOT COPY',0,'#DC2626')">DO NOT COPY</button>
        <button class="preset" onclick="setPreset('DRAFT',0,'#64748B')">DRAFT</button>
        <button class="preset" onclick="setPreset('SAMPLE',4,'#64748B')">SAMPLE (Repeating Tile Grid)</button>
        <button class="preset" onclick="setPreset('{UserName} | {ComputerName} | {DateTime}',0,'#DC2626')">Dynamic User Stamp</button>
      </div>
      <div class="form-row">
        <div>
          <label>Placement & Pattern Style</label>
          <select id="wm_place">
            <option value="0">Diagonal Single (Center -45°)</option>
            <option value="1">Horizontal Center (Flat 0°)</option>
            <option value="2">Top Center</option>
            <option value="3">Bottom Center</option>
            <option value="4">Repeating Tiled Grid (Multi-Text / Multi-Logo)</option>
          </select>
        </div>
        <div>
          <label>Content Mode</label>
          <select id="wm_mode">
            <option value="0">Text Only</option>
            <option value="1">Logo / Image Only</option>
            <option value="2">Both Logo Image & Text</option>
          </select>
        </div>
      </div>
      <div class="form-row">
        <div>
          <label>Watermark Text / Template</label>
          <input id="wm_text" value="CONFIDENTIAL">
        </div>
        <div>
          <label>Top Header Banner</label>
          <input id="wm_banner" value="CONFIDENTIAL - INTERNAL DISTRIBUTION ONLY">
        </div>
      </div>
      <div class="form-row">
        <div>
          <label>Opacity (<span id="op_val">18%</span>)</label>
          <input type="range" id="wm_opacity" min="0.05" max="0.80" step="0.01" value="0.18" oninput="op_val.textContent=Math.round(this.value*100)+'%'">
        </div>
        <div>
          <label>Font Size (pt) & Hex Color</label>
          <div style="display:flex;gap:6px">
            <input type="number" id="wm_size" value="36" style="width:80px">
            <input type="text" id="wm_color" value="#DC2626">
          </div>
        </div>
      </div>
      <div class="form-row">
        <div>
          <label>Upload Custom Logo / Watermark Image (PNG/JPG)</label>
          <input type="file" id="wm_file" accept="image/*" onchange="encodeImg(this)">
        </div>
        <div>
          <label>Logo Size (Width × Height pt)</label>
          <div style="display:flex;gap:6px">
            <input type="number" id="wm_imgw" value="160">
            <input type="number" id="wm_imgh" value="160">
          </div>
        </div>
      </div>
      <div style="display:flex;justify-content:space-between;align-items:center;margin-top:10px">
        <button onclick="savePolicy()">Save & Broadcast Policy to All Printers</button>
        <span id="wm_status" style="font-size:12px;color:#22c55e"></span>
      </div>
    </div>

    <!-- FORENSIC VERIFIER -->
    <div class="box">
      <h2>Forensic Page & QR Verifier</h2>
      <label>Paste ShortCode from Page Footer</label>
      <div style="display:flex;gap:8px;margin-bottom:14px">
        <input id="code" placeholder="e.g. k1-69-6AD1CEC2-41CE5F495D932B1B">
        <button onclick="verify()">Verify</button>
      </div>
      <div id="vr" style="margin-bottom:18px;color:var(--mute)">Enter a printed ShortCode above to verify HMAC authenticity.</div>
    </div>
  </div>

  <div class="box">
    <h2>Live Enterprise Print Audit Stream</h2>
    <table><thead><tr><th>Time</th><th>User</th><th>Workstation</th><th>Client IP</th><th>Pages</th><th>Cryptographic ShortCode</th></tr></thead>
    <tbody id="rows"></tbody></table>
  </div>
</main>
<script>
let currentBase64 = null;
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

function setPreset(txt, place, col){
  wm_text.value = txt; wm_place.value = place; wm_color.value = col;
}

function encodeImg(input){
  const f = input.files[0];
  if(!f) return;
  const r = new FileReader();
  r.onload = e => { currentBase64 = e.target.result; wm_status.textContent = 'Logo loaded ('+f.name+')'; };
  r.readAsDataURL(f);
}

async function loadPolicy(){
  try{
    const c = await fetch('/api/watermark/config').then(r=>r.json());
    wm_place.value = c.primaryPlacement ?? 0;
    wm_mode.value = c.contentMode ?? 0;
    wm_text.value = c.textTemplate ?? 'CONFIDENTIAL';
    wm_banner.value = c.topBannerText ?? '';
    wm_opacity.value = c.opacity ?? 0.18;
    op_val.textContent = Math.round((c.opacity ?? 0.18)*100)+'%';
    wm_size.value = c.fontSize ?? 36;
    wm_color.value = c.hexColor ?? '#DC2626';
    wm_imgw.value = c.imageWidthPt ?? 160;
    wm_imgh.value = c.imageHeightPt ?? 160;
    currentBase64 = c.imageBase64 ?? null;
  }catch(e){}
}

async function savePolicy(){
  const body = {
    enabled: true,
    primaryPlacement: parseInt(wm_place.value),
    contentMode: parseInt(wm_mode.value),
    showTopBanner: wm_banner.value.trim().length > 0,
    showBottomAuditFooter: true,
    showQrCode: true,
    textTemplate: wm_text.value,
    topBannerText: wm_banner.value,
    imageBase64: currentBase64,
    imageWidthPt: parseFloat(wm_imgw.value || 160),
    imageHeightPt: parseFloat(wm_imgh.value || 160),
    fontFamily: 'Arial',
    fontSize: parseFloat(wm_size.value || 36),
    opacity: parseFloat(wm_opacity.value || 0.18),
    directionAngle: -45.0,
    hexColor: wm_color.value || '#DC2626',
    tileStepX: 190.0,
    tileStepY: 135.0,
    bottomMarginPt: 45.0
  };
  await fetch('/api/watermark/config', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(body)});
  wm_status.textContent = '✓ Policy saved & active across all queues (' + new Date().toLocaleTimeString() + ')';
}

async function refresh(){
  try{
    const [s,l]=await Promise.all([fetch('/api/audit/stats').then(r=>r.json()),fetch('/api/audit/log?take=25').then(r=>r.json())]);
    s_total.textContent=s.totalJobsProcessed; s_24.textContent=s.jobsLast24Hours; s_users.textContent=s.distinctUsers;
    s_pcs.textContent=s.distinctWorkstations; s_pages.textContent=s.totalPages;
    rows.innerHTML=l.map(r=>`<tr><td>${esc(new Date(r.timestamp).toLocaleString())}</td><td>${esc(r.userName)}</td><td>${esc(r.computerName)}</td><td>${esc(r.clientIp)}</td><td>${esc(r.pageCount)}</td><td><code>${esc(r.shortCode)}</code></td></tr>`).join('');
    live.textContent='● Live · '+new Date().toLocaleTimeString();
  }catch(e){live.textContent='○ Server unreachable';}
}

async function verify(){
  const c=code.value.trim(); if(!c) return;
  const r=await fetch('/api/audit/verify/'+encodeURIComponent(c));
  if(r.status===404){vr.innerHTML='<span class="bad">No matching audit record found.</span>';return;}
  const j=await r.json(), x=j.record;
  vr.innerHTML=(j.hmacValid?'<span class="ok">✓ Authentic Cryptographic Record</span>':'<span class="bad">✗ HMAC Mismatch – Altered</span>')
    +`<br>User: <b>${esc(x.userName)}</b> | PC: <b>${esc(x.computerName)}</b> (${esc(x.clientIp)})<br>Printed: ${esc(new Date(x.timestamp).toLocaleString())} | Pages: ${esc(x.pageCount)}`;
}

loadPolicy(); refresh(); setInterval(refresh,3000);
</script></body></html>
""";
}