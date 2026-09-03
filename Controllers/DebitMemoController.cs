using System.Globalization;
using Accounting_System.Data;
using Accounting_System.Models;
using Accounting_System.Models.AccountsReceivable;
using Accounting_System.Models.Reports;
using Accounting_System.Models.ViewModels;
using Accounting_System.Repository;
using Accounting_System.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using System.Linq.Dynamic.Core;
using Microsoft.IdentityModel.Tokens;

namespace Accounting_System.Controllers
{
    [Authorize]
    public class DebitMemoController : Controller
    {
        private readonly ApplicationDbContext _dbContext;

        private readonly UserManager<IdentityUser> _userManager;

        private readonly DebitMemoRepo _debitMemoRepo;

        private readonly SalesInvoiceRepo _salesInvoiceRepo;

        private readonly ServiceInvoiceRepo _serviceInvoiceRepo;

        private readonly GeneralRepo _generalRepo;

        public DebitMemoController(ApplicationDbContext dbContext, UserManager<IdentityUser> userManager, DebitMemoRepo dmcmRepo, GeneralRepo generalRepo, SalesInvoiceRepo salesInvoiceRepo, ServiceInvoiceRepo serviceInvoiceRepo)
        {
            _dbContext = dbContext;
            this._userManager = userManager;
            _debitMemoRepo = dmcmRepo;
            _generalRepo = generalRepo;
            _salesInvoiceRepo = salesInvoiceRepo;
            _serviceInvoiceRepo = serviceInvoiceRepo;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> GetDebitMemos([FromForm] DataTablesParameters parameters, CancellationToken cancellationToken)
        {
            try
            {
                var debitMemos = await _debitMemoRepo.GetDebitMemosAsync(cancellationToken);
                // Search filter
                if (!string.IsNullOrEmpty(parameters.Search.Value))
                {
                    var searchValue = parameters.Search.Value.ToLower();
                    debitMemos = debitMemos
                        .Where(dm =>
                            dm.DebitMemoNo!.ToLower().Contains(searchValue) ||
                            dm.TransactionDate.ToString("MMM dd, yyyy").ToLower().Contains(searchValue) ||
                            dm.SalesInvoice?.SalesInvoiceNo?.ToLower().Contains(searchValue) == true ||
                            dm.ServiceInvoice?.ServiceInvoiceNo?.ToLower().Contains(searchValue) == true ||
                            dm.Source.ToLower().Contains(searchValue) ||
                            dm.DebitAmount.ToString(CultureInfo.InvariantCulture).Contains(searchValue) ||
                            dm.Remarks?.ToLower().Contains(searchValue) == true ||
                            dm.Description.ToLower().Contains(searchValue) ||
                            dm.CreatedBy!.ToLower().Contains(searchValue)
                            )
                        .ToList();
                }
                // Sorting
                if (parameters.Order != null && parameters.Order.Count > 0)
                {
                    var orderColumn = parameters.Order[0];
                    var columnName = parameters.Columns[orderColumn.Column].Data;
                    var sortDirection = orderColumn.Dir.ToLower() == "asc" ? "ascending" : "descending";
                    debitMemos = debitMemos
                        .AsQueryable()
                        .OrderBy($"{columnName} {sortDirection}")
                        .ToList();
                }
                var totalRecords = debitMemos.Count();
                var pagedData = debitMemos
                    .Skip(parameters.Start)
                    .Take(parameters.Length)
                    .ToList();
                return Json(new
                {
                    draw = parameters.Draw,
                    recordsTotal = totalRecords,
                    recordsFiltered = totalRecords,
                    data = pagedData
                });
            }
            catch (Exception ex)
            {
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAllDebitMemoIds(CancellationToken cancellationToken)
        {
            var debitMemoIds = await _dbContext.DebitMemos
                                     .Select(dm => dm.DebitMemoId) // Assuming Id is the primary key
                                     .ToListAsync(cancellationToken);
            return Json(debitMemoIds);
        }

        [HttpGet]
        public async Task<IActionResult> Create(CancellationToken cancellationToken)
        {
            var viewModel = new DebitMemo
            {
                SalesInvoices = await _dbContext.SalesInvoices
                    .Where(si => si.IsPosted)
                    .Select(si => new SelectListItem
                    {
                        Value = si.SalesInvoiceId.ToString(),
                        Text = si.SalesInvoiceNo
                    })
                    .ToListAsync(cancellationToken),
                ServiceInvoices = await _dbContext.ServiceInvoices
                    .Where(sv => sv.IsPosted)
                    .Select(sv => new SelectListItem
                    {
                        Value = sv.ServiceInvoiceId.ToString(),
                        Text = sv.ServiceInvoiceNo
                    })
                    .ToListAsync(cancellationToken)
            };

            return View(viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> Create(DebitMemo model, CancellationToken cancellationToken)
        {
            model.SalesInvoices = await _dbContext.SalesInvoices
                .Where(si => si.IsPosted)
                .Select(si => new SelectListItem
                {
                    Value = si.SalesInvoiceId.ToString(),
                    Text = si.SalesInvoiceNo
                })
                .ToListAsync(cancellationToken);

            model.ServiceInvoices = await _dbContext.ServiceInvoices
                .Where(sv => sv.IsPosted)
                .Select(sv => new SelectListItem
                {
                    Value = sv.ServiceInvoiceId.ToString(),
                    Text = sv.ServiceInvoiceNo
                })
                .ToListAsync(cancellationToken);

            var existingSv = await _dbContext.ServiceInvoices
                        .Include(sv => sv.Customer)
                        .FirstOrDefaultAsync(sv => sv.ServiceInvoiceId == model.ServiceInvoiceId, cancellationToken);

            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region -- checking for unposted DM or CM --

                        var existingSiDms = await _dbContext.DebitMemos
                                      .Where(dm => !dm.IsPosted && !dm.IsCanceled && !dm.IsVoided)
                                      .OrderBy(dm => dm.DebitMemoId)
                                      .ToListAsync(cancellationToken);
                        if (existingSiDms.Count > 0)
                        {
                            var dmNo = new List<string>();
                            foreach (var item in existingSiDms)
                            {
                                dmNo.Add(item.DebitMemoNo!);
                            }
                            ModelState.AddModelError("", $"Can’t proceed to create you have unposted DM. {string.Join(" , ", dmNo)}");
                            return View(model);
                        }

                        var existingSiCms = await _dbContext.CreditMemos
                                          .Where(cm => !cm.IsPosted && !cm.IsCanceled && !cm.IsVoided)
                                          .OrderBy(cm => cm.CreditMemoId)
                                          .ToListAsync(cancellationToken);

                        if (existingSiCms.Count > 0)
                        {
                            var cmNo = new List<string>();
                            foreach (var item in existingSiCms)
                            {
                                cmNo.Add(item.CreditMemoNo!);
                            }
                            ModelState.AddModelError("", $"Can’t proceed to create you have unposted CM. {string.Join(" , ", cmNo)}");
                            return View(model);
                        }

                    #endregion

                    #region --Validating the series--

                    var generateDmNo = await _debitMemoRepo.GenerateDMNo(cancellationToken);
                    var getLastNumber = long.Parse(generateDmNo.Substring(2));

                    if (getLastNumber > 9999999999)
                    {
                        TempData["error"] = "You reach the maximum Series Number";
                        return View(model);
                    }
                    var totalRemainingSeries = 9999999999 - getLastNumber;
                    if (getLastNumber >= 9999999899)
                    {
                        TempData["warning"] = $"Debit Memo created successfully, Warning {totalRemainingSeries} series number remaining";
                    }
                    else
                    {
                        TempData["success"] = "Debit Memo created successfully";
                    }

                    #endregion --Validating the series--

                    model.DebitMemoNo = generateDmNo;
                    model.CreatedBy = createdBy;

                    if (model.Source == "Sales Invoice")
                    {
                        model.ServiceInvoiceId = null;

                        model.DebitAmount = (decimal)(model.Quantity * model.AdjustedPrice)!;
                    }
                    else if (model.Source == "Service Invoice")
                    {
                        model.SalesInvoiceId = null;

                        #region --Retrieval of Services

                        model.ServicesId = existingSv?.ServicesId;

                        await _dbContext
                            .Services
                            .FirstOrDefaultAsync(s => s.ServiceId == model.ServicesId, cancellationToken);

                        #endregion --Retrieval of Services

                        model.DebitAmount = model.Amount ?? 0;
                    }

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Create new debit memo# {model.DebitMemoNo}", "Debit Memo", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.AddAsync(model, cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                 await transaction.RollbackAsync(cancellationToken);
                 TempData["error"] = ex.Message;
                 return RedirectToAction(nameof(Index));
                }
            }
            else
            {
                ModelState.AddModelError("", "The information you submitted is not valid!");
                return View(model);
            }
        }

        [HttpGet]
        public async Task<IActionResult> Print(int? id, CancellationToken cancellationToken)
        {
            if (id == null || !_dbContext.DebitMemos.Any())
            {
                return NotFound();
            }

            var debitMemo = await _dbContext.DebitMemos
                .Include(dm => dm.SalesInvoice)
                .ThenInclude(s => s!.Customer)
                .Include(dm => dm.SalesInvoice)
                .ThenInclude(s => s!.Product)
                .Include(dm => dm.ServiceInvoice)
                .ThenInclude(sv => sv!.Customer)
                .Include(dm => dm.ServiceInvoice)
                .ThenInclude(sv => sv!.Service)
                .FirstOrDefaultAsync(r => r.DebitMemoId == id, cancellationToken);
            if (debitMemo == null)
            {
                return NotFound();
            }
            return View(debitMemo);
        }

        public async Task<IActionResult> PrintedDM(int id, CancellationToken cancellationToken)
        {
            var findIdOfDm = await _debitMemoRepo.FindDM(id, cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            if (!findIdOfDm.IsPrinted)
            {

                #region --Audit Trail Recording

                if (findIdOfDm.OriginalSeriesNumber.IsNullOrEmpty() && findIdOfDm.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy, $"Printed original copy of dm# {findIdOfDm.DebitMemoNo}", "Debit Memo", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                findIdOfDm.IsPrinted = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return RedirectToAction(nameof(Print), new { id });
        }

        public async Task<IActionResult> Post(int id, ViewModelDMCM viewModelDmcm, CancellationToken cancellationToken)
        {
            var model = await _debitMemoRepo.FindDM(id, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedDate : DateTime.Now;
            try
            {
                if (!model.IsPosted)
                {
                    model.IsPosted = true;
                    model.PostedBy = createdBy;
                    model.PostedDate = date;

                    var accountTitlesDto = await _generalRepo.GetListOfAccountTitleDto(cancellationToken);
                    var arTradeReceivableTitle = accountTitlesDto.Find(c => c.AccountNumber == "101020100") ?? throw new ArgumentException("Account number: '101020100', Account title: 'AR-Trade Receivable' not found.");
                    var arNonTradeReceivableTitle = accountTitlesDto.Find(c => c.AccountNumber == "101020500") ?? throw new ArgumentException("Account number: '101020500', Account title: 'AR-Non Trade Receivable' not found.");
                    var arTradeCwt = accountTitlesDto.Find(c => c.AccountNumber == "101020200") ?? throw new ArgumentException("Account number: '101020200', Account title: 'AR-Trade Receivable - Creditable Withholding Tax' not found.");
                    var arTradeCwv = accountTitlesDto.Find(c => c.AccountNumber == "101020300") ?? throw new ArgumentException("Account number: '101020300', Account title: 'AR-Trade Receivable - Creditable Withholding Vat' not found.");
                    var (salesAcctNo, _) = _generalRepo.GetSalesAccountTitle(model.SalesInvoice!.Product!.ProductCode!);
                    var salesTitle = accountTitlesDto.Find(c => c.AccountNumber == salesAcctNo) ?? throw new ArgumentException($"Account title '{salesAcctNo}' not found.");
                    var vatOutputTitle = accountTitlesDto.Find(c => c.AccountNumber == "201030100") ?? throw new ArgumentException("Account number: '201030100', Account title: 'Vat - Output' not found.");


                    if (model.SalesInvoiceId != null)
                    {
                        #region --Retrieval of SI

                        var existingSi = await _dbContext
                            .SalesInvoices
                            .Include(c => c.Customer)
                            .Include(s => s.Product)
                            .FirstOrDefaultAsync(invoice => invoice.SalesInvoiceId == model.SalesInvoiceId, cancellationToken);

                        #endregion --Retrieval of SI

                        #region --Sales Book Recording(SI)--

                        var sales = new SalesBook();

                        if (model.SalesInvoice.Customer!.CustomerType == "Vatable")
                        {
                            sales.TransactionDate = model.TransactionDate;
                            sales.SerialNo = model.DebitMemoNo!;
                            sales.SoldTo = model.SalesInvoice.Customer.CustomerName;
                            sales.TinNo = model.SalesInvoice.Customer.CustomerTin;
                            sales.Address = model.SalesInvoice.Customer.CustomerAddress;
                            sales.Description = model.SalesInvoice.Product.ProductName;
                            sales.Amount = model.DebitAmount;
                            sales.VatableSales = _generalRepo.ComputeNetOfVat(sales.Amount);
                            sales.VatAmount = _generalRepo.ComputeVatAmount(sales.VatableSales);
                            //sales.Discount = model.Discount;
                            sales.NetSales = sales.VatableSales;
                            sales.CreatedBy = model.CreatedBy;
                            sales.CreatedDate = model.CreatedDate;
                            sales.DueDate = existingSi!.DueDate;
                            sales.DocumentId = model.SalesInvoiceId;
                        }
                        else if (model.SalesInvoice.Customer.CustomerType == "Exempt")
                        {
                            sales.TransactionDate = model.TransactionDate;
                            sales.SerialNo = model.DebitMemoNo!;
                            sales.SoldTo = model.SalesInvoice.Customer.CustomerName;
                            sales.TinNo = model.SalesInvoice.Customer.CustomerTin;
                            sales.Address = model.SalesInvoice.Customer.CustomerAddress;
                            sales.Description = model.SalesInvoice.Product.ProductName;
                            sales.Amount = model.DebitAmount;
                            sales.VatExemptSales = model.DebitAmount;
                            //sales.Discount = model.Discount;
                            sales.NetSales = sales.Amount;
                            sales.CreatedBy = model.CreatedBy;
                            sales.CreatedDate = model.CreatedDate;
                            sales.DueDate = existingSi!.DueDate;
                            sales.DocumentId = model.SalesInvoiceId;
                        }
                        else
                        {
                            sales.TransactionDate = model.TransactionDate;
                            sales.SerialNo = model.DebitMemoNo!;
                            sales.SoldTo = model.SalesInvoice.Customer.CustomerName;
                            sales.TinNo = model.SalesInvoice.Customer.CustomerTin;
                            sales.Address = model.SalesInvoice.Customer.CustomerAddress;
                            sales.Description = model.SalesInvoice.Product.ProductName;
                            sales.Amount = model.DebitAmount;
                            sales.ZeroRated = model.DebitAmount;
                            //sales.Discount = model.Discount;
                            sales.NetSales = sales.Amount;
                            sales.CreatedBy = model.CreatedBy;
                            sales.CreatedDate = model.CreatedDate;
                            sales.DueDate = existingSi!.DueDate;
                            sales.DocumentId = model.SalesInvoiceId;
                        }
                        await _dbContext.AddAsync(sales, cancellationToken);

                        #endregion --Sales Book Recording(SI)--

                        #region --General Ledger Book Recording(SI)--

                        decimal withHoldingTaxAmount = 0;
                        decimal withHoldingVatAmount = 0;
                        decimal netOfVatAmount;
                        decimal vatAmount = 0;
                        if (model.SalesInvoice.Customer.CustomerType == CS.VatType_Vatable)
                        {
                            netOfVatAmount = _generalRepo.ComputeNetOfVat(model.DebitAmount);
                            vatAmount = _generalRepo.ComputeVatAmount(netOfVatAmount);
                        }
                        else
                        {
                            netOfVatAmount = model.DebitAmount;
                        }
                        if (model.SalesInvoice.Customer.WithHoldingTax)
                        {
                            withHoldingTaxAmount = _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.01m);
                        }
                        if (model.SalesInvoice.Customer.WithHoldingVat)
                        {
                            withHoldingVatAmount = _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.05m);
                        }

                        var ledgers = new List<GeneralLedgerBook>
                        {
                            new GeneralLedgerBook
                            {
                                Date = model.TransactionDate,
                                Reference = model.DebitMemoNo!,
                                Description = model.SalesInvoice.Product.ProductName,
                                AccountNo = arTradeReceivableTitle.AccountNumber,
                                AccountTitle = arTradeReceivableTitle.AccountName,
                                Debit = model.DebitAmount - (withHoldingTaxAmount + withHoldingVatAmount),
                                Credit = 0,
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            }
                        };

                        if (withHoldingTaxAmount > 0)
                        {
                            ledgers.Add(
                                new GeneralLedgerBook
                                {
                                    Date = model.TransactionDate,
                                    Reference = model.DebitMemoNo!,
                                    Description = model.SalesInvoice.Product.ProductName,
                                    AccountNo = arTradeCwt.AccountNumber,
                                    AccountTitle = arTradeCwt.AccountNumber,
                                    Debit = withHoldingTaxAmount,
                                    Credit = 0,
                                    CreatedBy = model.CreatedBy,
                                    CreatedDate = model.CreatedDate
                                }
                            );
                        }
                        if (withHoldingVatAmount > 0)
                        {
                            ledgers.Add(
                                new GeneralLedgerBook
                                {
                                    Date = model.TransactionDate,
                                    Reference = model.DebitMemoNo!,
                                    Description = model.SalesInvoice.Product.ProductName,
                                    AccountNo = arTradeCwv.AccountNumber,
                                    AccountTitle = arTradeCwv.AccountName,
                                    Debit = withHoldingVatAmount,
                                    Credit = 0,
                                    CreatedBy = model.CreatedBy,
                                    CreatedDate = model.CreatedDate
                                }
                            );
                        }
                        ledgers.Add(
                            new GeneralLedgerBook
                            {
                                Date = model.TransactionDate,
                                Reference = model.DebitMemoNo!,
                                Description = model.SalesInvoice.Product.ProductName,
                                AccountNo = salesTitle.AccountNumber,
                                AccountTitle = salesTitle.AccountNumber,
                                Debit = 0,
                                CreatedBy = model.CreatedBy,
                                Credit = netOfVatAmount,
                                CreatedDate = model.CreatedDate
                            }
                        );
                        if (vatAmount > 0)
                        {
                            ledgers.Add(
                                new GeneralLedgerBook
                                {
                                    Date = model.TransactionDate,
                                    Reference = model.DebitMemoNo!,
                                    Description = model.SalesInvoice.Product.ProductName,
                                    AccountNo = vatOutputTitle.AccountNumber,
                                    AccountTitle = vatOutputTitle.AccountName,
                                    Debit = 0,
                                    Credit = vatAmount,
                                    CreatedBy = model.CreatedBy,
                                    CreatedDate = model.CreatedDate
                                }
                            );
                        }

                        if (!_generalRepo.IsJournalEntriesBalanced(ledgers))
                        {
                            throw new ArgumentException("Debit and Credit is not equal, check your entries.");
                        }

                        await _dbContext.GeneralLedgerBooks.AddRangeAsync(ledgers, cancellationToken);

                        #endregion --General Ledger Book Recording(SI)--
                    }

                    if (model.ServiceInvoiceId != null)
                    {
                        var existingSv = await _dbContext.ServiceInvoices
                            .Include(sv => sv.Customer)
                            .Include(sv => sv.Service)
                            .FirstOrDefaultAsync(sv => sv.ServiceInvoiceId == model.ServiceInvoiceId, cancellationToken);

                        #region --SV Computation--

                        viewModelDmcm.Period = DateOnly.FromDateTime(model.CreatedDate) >= model.Period ? DateOnly.FromDateTime(model.CreatedDate) : model.Period.AddMonths(1).AddDays(-1);

                        if (existingSv!.Customer!.CustomerType == "Vatable")
                        {
                            viewModelDmcm.Total = model.Amount ?? 0  - existingSv.Discount;
                            viewModelDmcm.NetAmount = _generalRepo.ComputeNetOfVat(viewModelDmcm.Total);
                            viewModelDmcm.VatAmount = _generalRepo.ComputeVatAmount(viewModelDmcm.NetAmount);
                            viewModelDmcm.WithholdingTaxAmount = viewModelDmcm.NetAmount * (existingSv.Customer.WithHoldingTax ? existingSv.Service!.Percent / 100m : 0);
                            if (existingSv.Customer.WithHoldingVat)
                            {
                                viewModelDmcm.WithholdingVatAmount = viewModelDmcm.NetAmount * 0.05m;
                            }
                        }
                        else
                        {
                            viewModelDmcm.NetAmount = model.Amount ?? 0 - existingSv.Discount;
                            viewModelDmcm.WithholdingTaxAmount = viewModelDmcm.NetAmount * (existingSv.Customer.WithHoldingTax ? existingSv.Service!.Percent / 100m : 0);
                            if (existingSv.Customer.WithHoldingVat)
                            {
                                viewModelDmcm.WithholdingVatAmount = viewModelDmcm.NetAmount * 0.05m;
                            }
                        }

                        #endregion --SV Computation--

                        #region --Sales Book Recording(SV)--

                        var sales = new SalesBook();

                        if (model.ServiceInvoice!.Customer!.CustomerType == "Vatable")
                        {
                            sales.TransactionDate = viewModelDmcm.Period;
                            sales.SerialNo = model.DebitMemoNo!;
                            sales.SoldTo = model.ServiceInvoice.Customer.CustomerName;
                            sales.TinNo = model.ServiceInvoice.Customer.CustomerTin;
                            sales.Address = model.ServiceInvoice.Customer.CustomerAddress;
                            sales.Description = model.ServiceInvoice.Service!.Name;
                            sales.Amount = viewModelDmcm.Total;
                            sales.VatAmount = viewModelDmcm.VatAmount;
                            sales.VatableSales = viewModelDmcm.Total / 1.12m;
                            //sales.Discount = model.Discount;
                            sales.NetSales = viewModelDmcm.NetAmount;
                            sales.CreatedBy = model.CreatedBy;
                            sales.CreatedDate = model.CreatedDate;
                            sales.DueDate = existingSv.DueDate;
                            sales.DocumentId = existingSv.ServiceInvoiceId;
                        }
                        else if (model.ServiceInvoice.Customer.CustomerType == "Exempt")
                        {
                            sales.TransactionDate = viewModelDmcm.Period;
                            sales.SerialNo = model.DebitMemoNo!;
                            sales.SoldTo = model.ServiceInvoice.Customer.CustomerName;
                            sales.TinNo = model.ServiceInvoice.Customer.CustomerTin;
                            sales.Address = model.ServiceInvoice.Customer.CustomerAddress;
                            sales.Description = model.ServiceInvoice.Service!.Name;
                            sales.Amount = viewModelDmcm.Total;
                            sales.VatExemptSales = viewModelDmcm.Total;
                            //sales.Discount = model.Discount;
                            sales.NetSales = viewModelDmcm.NetAmount;
                            sales.CreatedBy = model.CreatedBy;
                            sales.CreatedDate = model.CreatedDate;
                            sales.DueDate = existingSv.DueDate;
                            sales.DocumentId = existingSv.ServiceInvoiceId;
                        }
                        else
                        {
                            sales.TransactionDate = viewModelDmcm.Period;
                            sales.SerialNo = model.DebitMemoNo!;
                            sales.SoldTo = model.ServiceInvoice.Customer.CustomerName;
                            sales.TinNo = model.ServiceInvoice.Customer.CustomerTin;
                            sales.Address = model.ServiceInvoice.Customer.CustomerAddress;
                            sales.Description = model.ServiceInvoice.Service!.Name;
                            sales.Amount = viewModelDmcm.Total;
                            sales.ZeroRated = viewModelDmcm.Total;
                            //sales.Discount = model.Discount;
                            sales.NetSales = viewModelDmcm.NetAmount;
                            sales.CreatedBy = model.CreatedBy;
                            sales.CreatedDate = model.CreatedDate;
                            sales.DueDate = existingSv.DueDate;
                            sales.DocumentId = existingSv.ServiceInvoiceId;
                        }
                        await _dbContext.AddAsync(sales, cancellationToken);

                        #endregion --Sales Book Recording(SV)--

                        #region --General Ledger Book Recording(SV)--

                        var ledgers = new List<GeneralLedgerBook>
                        {
                            new GeneralLedgerBook
                            {
                                Date = model.TransactionDate,
                                Reference = model.DebitMemoNo!,
                                Description = model.ServiceInvoice.Service.Name,
                                AccountNo = arNonTradeReceivableTitle.AccountNumber,
                                AccountTitle = arNonTradeReceivableTitle.AccountName,
                                Debit = viewModelDmcm.Total - (viewModelDmcm.WithholdingTaxAmount + viewModelDmcm.WithholdingVatAmount),
                                Credit = 0,
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            }
                        };

                        if (viewModelDmcm.WithholdingTaxAmount > 0)
                        {
                            ledgers.Add(
                                new GeneralLedgerBook
                                {
                                    Date = model.TransactionDate,
                                    Reference = model.DebitMemoNo!,
                                    Description = model.ServiceInvoice.Service.Name,
                                    AccountNo = arTradeCwt.AccountNumber,
                                    AccountTitle = arTradeCwt.AccountName,
                                    Debit = viewModelDmcm.WithholdingTaxAmount,
                                    Credit = 0,
                                    CreatedBy = model.CreatedBy,
                                    CreatedDate = model.CreatedDate
                                }
                            );
                        }
                        if (viewModelDmcm.WithholdingVatAmount > 0)
                        {
                            ledgers.Add(
                                new GeneralLedgerBook
                                {
                                    Date = model.TransactionDate,
                                    Reference = model.DebitMemoNo!,
                                    Description = model.ServiceInvoice.Service.Name,
                                    AccountNo = arTradeCwv.AccountNumber,
                                    AccountTitle = arTradeCwv.AccountName,
                                    Debit = viewModelDmcm.WithholdingVatAmount,
                                    Credit = 0,
                                    CreatedBy = model.CreatedBy,
                                    CreatedDate = model.CreatedDate
                                }
                            );
                        }

                        if (viewModelDmcm.Total > 0)
                        {
                            ledgers.Add(new GeneralLedgerBook
                            {
                                Date = model.TransactionDate,
                                Reference = model.DebitMemoNo!,
                                Description = model.ServiceInvoice.Service.Name,
                                AccountNo = model.ServiceInvoice.Service.CurrentAndPreviousNo!,
                                AccountTitle = model.ServiceInvoice.Service.CurrentAndPreviousTitle!,
                                Debit = 0,
                                Credit = viewModelDmcm.Total / 1.12m,
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            });
                        }

                        if (viewModelDmcm.VatAmount > 0)
                        {
                            ledgers.Add(
                                new GeneralLedgerBook
                                {
                                    Date = model.TransactionDate,
                                    Reference = model.DebitMemoNo!,
                                    Description = model.ServiceInvoice.Service.Name,
                                    AccountNo = vatOutputTitle.AccountNumber,
                                    AccountTitle = vatOutputTitle.AccountName,
                                    Debit = 0,
                                    Credit = viewModelDmcm.VatAmount,
                                    CreatedBy = model.CreatedBy,
                                    CreatedDate = model.CreatedDate
                                }
                            );
                        }

                        if (!_generalRepo.IsJournalEntriesBalanced(ledgers))
                        {
                            throw new ArgumentException("Debit and Credit is not equal, check your entries.");
                        }

                        await _dbContext.GeneralLedgerBooks.AddRangeAsync(ledgers, cancellationToken);

                        #endregion --General Ledger Book Recording(SV)--
                    }

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Posted debit memo# {model.DebitMemoNo}", "Debit Memo", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Debit Memo has been Posted.";
                }
                return RedirectToAction(nameof(Print), new { id });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Print), new { id });
            }
        }

        public async Task<IActionResult> Void(int id, CancellationToken cancellationToken)
        {
            var model = await _dbContext.DebitMemos.FirstOrDefaultAsync(x => x.DebitMemoId == id, cancellationToken);

            if (model != null)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedDate : DateTime.Now;
                try
                {
                    if (!model.IsVoided)
                    {
                        if (model.IsPosted)
                        {
                            model.IsPosted = false;
                        }

                        model.IsVoided = true;
                        model.VoidedBy = createdBy;
                        model.VoidedDate = date;

                        await _generalRepo.RemoveRecords<SalesBook>(crb => crb.SerialNo == model.DebitMemoNo, cancellationToken);
                        await _generalRepo.RemoveRecords<GeneralLedgerBook>(gl => gl.Reference == model.DebitMemoNo, cancellationToken);

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Voided debit memo# {model.DebitMemoNo}", "Debit Memo", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Debit Memo has been Voided.";
                    }
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                    return RedirectToAction(nameof(Index));
                }
            }

            return NotFound();
        }

        public async Task<IActionResult> Cancel(int id, string cancellationRemarks, CancellationToken cancellationToken)
        {
            var model = await _dbContext.DebitMemos.FirstOrDefaultAsync(x => x.DebitMemoId == id, cancellationToken);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.CanceledBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.CanceledDate : DateTime.Now;
            try
            {
                if (model != null)
                {
                    if (!model.IsCanceled)
                    {
                        model.IsCanceled = true;
                        model.CanceledBy = createdBy;
                        model.CanceledDate = date;
                        model.CancellationRemarks = cancellationRemarks;

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Cancelled debit memo# {model.DebitMemoNo}", "Debit Memo", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Debit Memo has been Cancelled.";
                    }
                    return RedirectToAction(nameof(Index));
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }

            return NotFound();
        }

        [HttpGet]
        public async Task<JsonResult> GetSVDetails(int svId, CancellationToken cancellationToken)
        {
            var model = await _dbContext.ServiceInvoices.FirstOrDefaultAsync(sv => sv.ServiceInvoiceId == svId, cancellationToken);
            if (model != null)
            {
                return Json(new
                {
                    model.Period,
                    model.Amount
                });
            }

            return Json(null);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int? id, CancellationToken cancellationToken)
        {
            if (id == null || !_dbContext.DebitMemos.Any())
            {
                return NotFound();
            }

            var debitMemo = await _dbContext.DebitMemos
                .Include(cm => cm.SalesInvoice)
                .Include(cm => cm.ServiceInvoice)
                .ThenInclude(sv => sv!.Customer)
                .Include(cm => cm.ServiceInvoice)
                .ThenInclude(sv => sv!.Service)
                .FirstOrDefaultAsync(r => r.DebitMemoId == id, cancellationToken);

            if (debitMemo == null)
            {
                return NotFound();
            }

            debitMemo.SalesInvoices = await _dbContext.SalesInvoices
                .Where(si => si.IsPosted)
                .Select(si => new SelectListItem
                {
                    Value = si.SalesInvoiceId.ToString(),
                    Text = si.SalesInvoiceNo
                })
                .ToListAsync(cancellationToken);
            debitMemo.ServiceInvoices = await _dbContext.ServiceInvoices
                .Where(sv => sv.IsPosted)
                .Select(sv => new SelectListItem
                {
                    Value = sv.ServiceInvoiceId.ToString(),
                    Text = sv.ServiceInvoiceNo
                })
                .ToListAsync(cancellationToken);

            return View(debitMemo);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(DebitMemo model, CancellationToken cancellationToken)
        {
            var existingSv = await _dbContext.ServiceInvoices
                .Include(sv => sv.Customer)
                .FirstOrDefaultAsync(sv => sv.ServiceInvoiceId == model.ServiceInvoiceId, cancellationToken);

            var existingDm = await _dbContext.DebitMemos
                .Include(cm => cm.SalesInvoice)
                .Include(cm => cm.ServiceInvoice)
                .ThenInclude(sv => sv!.Customer)
                .Include(cm => cm.ServiceInvoice)
                .ThenInclude(sv => sv!.Service)
                .FirstOrDefaultAsync(r => r.DebitMemoId == model.DebitMemoId, cancellationToken);

            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    if (model.Source == "Sales Invoice")
                    {
                        model.ServiceInvoiceId = null;

                        #region -- Saving Default Enries --

                        existingDm!.TransactionDate = model.TransactionDate;
                        existingDm.SalesInvoiceId = model.SalesInvoiceId;
                        existingDm.Quantity = model.Quantity;
                        existingDm.AdjustedPrice = model.AdjustedPrice;
                        existingDm.Description = model.Description;
                        existingDm.Remarks = model.Remarks;
                        existingDm.DebitAmount = (decimal)(model.Quantity * model.AdjustedPrice)!;

                        #endregion -- Saving Default Enries --
                    }
                    else if (model.Source == "Service Invoice")
                    {
                        model.SalesInvoiceId = null;

                        #region -- Saving Default Enries --

                        existingDm!.TransactionDate = model.TransactionDate;
                        existingDm.ServiceInvoiceId = model.ServiceInvoiceId;
                        existingDm.ServicesId = existingSv!.ServicesId;
                        existingDm.Period = model.Period;
                        existingDm.Amount = model.Amount;
                        existingDm.Description = model.Description;
                        existingDm.Remarks = model.Remarks;
                        existingDm.DebitAmount = model.Amount ?? 0;

                        #endregion -- Saving Default Enries --
                    }
                    if (_dbContext.ChangeTracker.HasChanges())
                    {
                        #region --Audit Trail Recording

                        if (existingDm!.OriginalSeriesNumber.IsNullOrEmpty() && existingDm.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Edit debit memo# {existingDm.DebitMemoNo}", "Debit Memo", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Debit Memo edited successfully";
                        return RedirectToAction(nameof(Index));
                    }
                    else
                    {
                        throw new InvalidOperationException("No data changes!");
                    }
                }
                catch (Exception ex)
                {
                 await transaction.RollbackAsync(cancellationToken);
                 existingDm!.SalesInvoices = await _dbContext.SalesInvoices
                     .Where(si => si.IsPosted)
                     .Select(si => new SelectListItem
                     {
                         Value = si.SalesInvoiceId.ToString(),
                         Text = si.SalesInvoiceNo
                     })
                     .ToListAsync(cancellationToken);
                 existingDm.ServiceInvoices = await _dbContext.ServiceInvoices
                     .Where(sv => sv.IsPosted)
                     .Select(sv => new SelectListItem
                     {
                         Value = sv.ServiceInvoiceId.ToString(),
                         Text = sv.ServiceInvoiceNo
                     })
                     .ToListAsync(cancellationToken);
                 TempData["error"] = ex.Message;
                 return View(existingDm);
                }
            }

            ModelState.AddModelError("", "The information you submitted is not valid!");
            existingDm!.SalesInvoices = await _dbContext.SalesInvoices
                .Where(si => si.IsPosted)
                .Select(si => new SelectListItem
                {
                    Value = si.SalesInvoiceId.ToString(),
                    Text = si.SalesInvoiceNo
                })
                .ToListAsync(cancellationToken);
            existingDm.ServiceInvoices = await _dbContext.ServiceInvoices
                .Where(sv => sv.IsPosted)
                .Select(sv => new SelectListItem
                {
                    Value = sv.ServiceInvoiceId.ToString(),
                    Text = sv.ServiceInvoiceNo
                })
                .ToListAsync(cancellationToken);
            return View(existingDm);
        }
    }
}
