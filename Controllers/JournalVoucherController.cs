using Accounting_System.Data;
using Accounting_System.Models;
using Accounting_System.Models.AccountsPayable;
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
    public class JournalVoucherController : Controller
    {
        private readonly ApplicationDbContext _dbContext;

        private readonly UserManager<IdentityUser> _userManager;

        private readonly JournalVoucherRepo _journalVoucherRepo;

        private readonly CheckVoucherRepo _checkVoucherRepo;

        private readonly ReceivingReportRepo _receivingReportRepo;

        private readonly PurchaseOrderRepo _purchaseOrderRepo;

        private readonly GeneralRepo _generalRepo;

        public JournalVoucherController(ApplicationDbContext dbContext, UserManager<IdentityUser> userManager, JournalVoucherRepo journalVoucherRepo, GeneralRepo generalRepo, CheckVoucherRepo checkVoucherRepo, ReceivingReportRepo receivingReportRepo, PurchaseOrderRepo purchaseOrderRepo)
        {
            _dbContext = dbContext;
            _userManager = userManager;
            _journalVoucherRepo = journalVoucherRepo;
            _generalRepo = generalRepo;
            _checkVoucherRepo = checkVoucherRepo;
            _receivingReportRepo = receivingReportRepo;
            _purchaseOrderRepo = purchaseOrderRepo;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> GetJournalVouchers([FromForm] DataTablesParameters parameters, CancellationToken cancellationToken)
        {
            try
            {
                var journalVouchers = await _journalVoucherRepo.GetJournalVouchersAsync(cancellationToken);
                // Search filter
                if (!string.IsNullOrEmpty(parameters.Search.Value))
                {
                    var searchValue = parameters.Search.Value.ToLower();
                    journalVouchers = journalVouchers
                        .Where(jv =>
                            jv.JournalVoucherHeaderNo!.ToLower().Contains(searchValue) ||
                            jv.Date.ToString("MMM dd, yyyy").ToLower().Contains(searchValue) ||
                            jv.References?.ToLower().Contains(searchValue) == true ||
                            jv.Particulars.ToLower().Contains(searchValue) ||
                            jv.CRNo?.ToLower().Contains(searchValue) == true ||
                            jv.JVReason.ToLower().Contains(searchValue) ||
                            jv.CheckVoucherHeader?.CheckVoucherHeaderNo?.ToLower().Contains(searchValue) == true ||
                            jv.CreatedBy!.ToLower().Contains(searchValue)
                            )
                        .ToList();
                }
                // Sorting
                if (parameters.Order != null && parameters.Order.Count > 0)
                {
                    var orderColumn = parameters.Order[0];
                    var columnName = parameters.Columns[orderColumn.Column].Data;
                    var sortDirection = orderColumn.Dir.ToLower() == "asc" ? "ascending" : "descending";
                    journalVouchers = journalVouchers
                        .AsQueryable()
                        .OrderBy($"{columnName} {sortDirection}")
                        .ToList();
                }
                var totalRecords = journalVouchers.Count();
                var pagedData = journalVouchers
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
        public async Task<IActionResult> GetAllJournalVoucherIds(CancellationToken cancellationToken)
        {
            var journalVoucherIds = await _dbContext.JournalVoucherHeaders
                                     .Select(jv => jv.JournalVoucherHeaderId) // Assuming Id is the primary key
                                     .ToListAsync(cancellationToken);
            return Json(journalVoucherIds);
        }

        [HttpGet]
        public async Task<IActionResult> Create(CancellationToken cancellationToken)
        {
            var viewModel = new JournalVoucherVM
            {
                Header = new JournalVoucherHeader(),
                Details = new List<JournalVoucherDetail>()
            };

            viewModel.Header.COA = await _dbContext.ChartOfAccounts
                .Where(coa => !coa.HasChildren)
                .OrderBy(coa => coa.AccountId)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);
            viewModel.Header.CheckVoucherHeaders = await _dbContext.CheckVoucherHeaders
                .Where(cvh => cvh.IsPosted)
                .OrderBy(c => c.CheckVoucherHeaderId)
                .Select(cvh => new SelectListItem
                {
                    Value = cvh.CheckVoucherHeaderId.ToString(),
                    Text = cvh.CheckVoucherHeaderNo
                })
                .ToListAsync(cancellationToken);

            return View(viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> Create(JournalVoucherVM? model, string[] accountNumber, decimal[]? debit, decimal[]? credit, CancellationToken cancellationToken)
        {
            model!.Header!.COA = await _dbContext.ChartOfAccounts
                .Where(coa => !coa.HasChildren)
                .OrderBy(coa => coa.AccountId)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);

            model.Header.CheckVoucherHeaders = await _dbContext.CheckVoucherHeaders
                .OrderBy(c => c.CheckVoucherHeaderId)
                .Select(cvh => new SelectListItem
                {
                    Value = cvh.CheckVoucherHeaderId.ToString(),
                    Text = cvh.CheckVoucherHeaderNo
                })
                .ToListAsync(cancellationToken);

            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Validating series

                    var generateJvNo = await _journalVoucherRepo.GenerateJVNo(cancellationToken);
                    var getLastNumber = long.Parse(generateJvNo.Substring(2));

                    if (getLastNumber > 9999999999)
                    {
                        TempData["error"] = "You reached the maximum Series Number";
                        return View(model);
                    }

                    var totalRemainingSeries = 9999999999 - getLastNumber;
                    if (getLastNumber >= 9999999899)
                    {
                        TempData["warning"] = $"Check Voucher created successfully, Warning {totalRemainingSeries} series numbers remaining";
                    }
                    else
                    {
                        TempData["success"] = "Check Voucher created successfully";
                    }

                    #endregion --Validating series

                    #region --Saving the default entries

                    //JV Header Entry
                    model.Header.JournalVoucherHeaderNo = generateJvNo;
                    model.Header.CreatedBy = createdBy;

                    await _dbContext.AddAsync(model.Header, cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    #endregion --Saving the default entries

                    #region --CV Details Entry

                    var cvDetails = new List<JournalVoucherDetail>();

                    var totalDebit = 0m;
                    var totalCredit = 0m;
                    for (int i = 0; i < accountNumber.Length; i++)
                    {
                        var currentAccountNumber = accountNumber[i];
                        var accountTitle = await _dbContext.ChartOfAccounts
                            .FirstOrDefaultAsync(coa => coa.AccountNumber == currentAccountNumber, cancellationToken);
                        var currentDebit = debit![i];
                        var currentCredit = credit![i];
                        totalDebit += debit[i];
                        totalCredit += credit[i];

                        cvDetails.Add(
                            new JournalVoucherDetail
                            {
                                AccountNo = currentAccountNumber,
                                AccountName = accountTitle!.AccountName,
                                TransactionNo = generateJvNo,
                                Debit = currentDebit,
                                Credit = currentCredit,
                                JournalVoucherHeaderId = model.Header.JournalVoucherHeaderId
                            }
                        );
                    }
                    if (totalDebit != totalCredit)
                    {
                        TempData["error"] = "The debit and credit should be equal!";
                        return View(model);
                    }

                    await _dbContext.JournalVoucherDetails.AddRangeAsync(cvDetails, cancellationToken);

                    #endregion --CV Details Entry

                    #region --Audit Trail Recording

                    if (model.Header.OriginalSeriesNumber.IsNullOrEmpty() && model.Header.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Create new journal voucher# {model.Header.JournalVoucherHeaderNo}", "Journal Voucher", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

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

            TempData["error"] = "The information you submitted is not valid!";
            return View(model);
        }

        public async Task<IActionResult> GetCV(int id, CancellationToken cancellationToken)
        {
            var model = await _dbContext.CheckVoucherHeaders
                .Include(s => s.Supplier)
                .Include(cvd => cvd.Details)
                .FirstOrDefaultAsync(cvh => cvh.CheckVoucherHeaderId == id, cancellationToken);

            if (model != null)
            {
                return Json(new
                {
                    CVNo = model.CheckVoucherHeaderNo,
                    model.Date,
                    Name = model.Supplier!.SupplierName,
                    Address = model.Supplier.SupplierAddress,
                    TinNo = model.Supplier.SupplierTin,
                    model.PONo,
                    model.SINo,
                    model.Payee,
                    Amount = model.Total,
                    model.Particulars,
                    model.CheckNo,
                    AccountNo = model.Details.Select(jvd => jvd.AccountNo),
                    AccountName = model.Details.Select(jvd => jvd.AccountName),
                    Debit = model.Details.Select(jvd => jvd.Debit),
                    Credit = model.Details.Select(jvd => jvd.Credit),
                    TotalDebit = model.Details.Select(cvd => cvd.Debit).Sum(),
                    TotalCredit = model.Details.Select(cvd => cvd.Credit).Sum(),
                });
            }

            return Json(null);
        }

        [HttpGet]
        public async Task<IActionResult> Print(int? id, CancellationToken cancellationToken)
        {
            if (id == null)
            {
                return NotFound();
            }

            var header = await _dbContext.JournalVoucherHeaders
                .Include(cv => cv.CheckVoucherHeader)
                .ThenInclude(supplier => supplier!.Supplier)
                .FirstOrDefaultAsync(jvh => jvh.JournalVoucherHeaderId == id.Value, cancellationToken);

            if (header == null)
            {
                return NotFound();
            }

            var details = await _dbContext.JournalVoucherDetails
                .Where(jvd => jvd.TransactionNo == header.JournalVoucherHeaderNo)
                .ToListAsync(cancellationToken);

            //if (header.Category == "Trade")
            //{
            //    var siArray = new string[header.RRNo.Length];
            //    for (int i = 0; i < header.RRNo.Length; i++)
            //    {
            //        var rrValue = header.RRNo[i];

            //        var rr = await _dbContext.ReceivingReports
            //                    .FirstOrDefaultAsync(p => p.RRNo == rrValue);

            //        siArray[i] = rr.SupplierInvoiceNumber;
            //    }

            //    ViewBag.SINoArray = siArray;
            //}

            var viewModel = new JournalVoucherVM
            {
                Header = header,
                Details = details
            };

            return View(viewModel);
        }

        public async Task<IActionResult> Printed(int id, CancellationToken cancellationToken)
        {
            var jv = await _dbContext.JournalVoucherHeaders.FirstOrDefaultAsync(x => x.JournalVoucherHeaderId == id, cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            if (jv != null && !jv.IsPrinted)
            {

                #region --Audit Trail Recording

                if (jv.OriginalSeriesNumber.IsNullOrEmpty() && jv.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy, $"Printed original copy of jv# {jv.JournalVoucherHeaderNo}", "Journal Voucher", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                jv.IsPrinted = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return RedirectToAction(nameof(Print), new { id });
        }

        public async Task<IActionResult> Post(int id, CancellationToken cancellationToken)
        {
            var modelHeader = await _dbContext.JournalVoucherHeaders.FirstOrDefaultAsync(x => x.JournalVoucherHeaderId == id, cancellationToken);

            if (modelHeader != null)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = !modelHeader.OriginalSeriesNumber.IsNullOrEmpty() && modelHeader.OriginalDocumentId != 0 ? modelHeader.PostedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                var date = !modelHeader.OriginalSeriesNumber.IsNullOrEmpty() && modelHeader.OriginalDocumentId != 0 ? modelHeader.PostedDate : DateTime.Now;
                try
                {
                    var modelDetails = await _dbContext.JournalVoucherDetails.Where(jvd => jvd.TransactionNo == modelHeader.JournalVoucherHeaderNo).ToListAsync(cancellationToken);
                    if (!modelHeader.IsPosted)
                    {
                        modelHeader.IsPosted = true;
                        modelHeader.PostedBy = createdBy;
                        modelHeader.PostedDate = date;

                        #region --General Ledger Book Recording(GL)--

                        var accountTitlesDto = await _generalRepo.GetListOfAccountTitleDto(cancellationToken);
                        var ledgers = new List<GeneralLedgerBook>();
                        foreach (var details in modelDetails)
                        {
                            var account = accountTitlesDto.Find(c => c.AccountNumber == details.AccountNo) ?? throw new ArgumentException($"Account number '{details.AccountNo}', Account title '{details.AccountName}' not found.");
                            ledgers.Add(
                                    new GeneralLedgerBook
                                    {
                                        Date = modelHeader.Date,
                                        Reference = modelHeader.JournalVoucherHeaderNo!,
                                        Description = modelHeader.Particulars,
                                        AccountNo = account.AccountNumber,
                                        AccountTitle = account.AccountName,
                                        Debit = details.Debit,
                                        Credit = details.Credit,
                                        CreatedBy = modelHeader.CreatedBy,
                                        CreatedDate = modelHeader.CreatedDate
                                    }
                                );
                        }

                        if (!_generalRepo.IsJournalEntriesBalanced(ledgers))
                        {
                            throw new ArgumentException("Debit and Credit is not equal, check your entries.");
                        }

                        await _dbContext.GeneralLedgerBooks.AddRangeAsync(ledgers, cancellationToken);

                        #endregion --General Ledger Book Recording(GL)--

                        #region --Journal Book Recording(JV)--

                        var journalBook = new List<JournalBook>();
                        foreach (var details in modelDetails)
                        {
                            journalBook.Add(
                                    new JournalBook
                                    {
                                        Date = modelHeader.Date,
                                        Reference = modelHeader.JournalVoucherHeaderNo!,
                                        Description = modelHeader.Particulars,
                                        AccountTitle = details.AccountNo + " " + details.AccountName,
                                        Debit = details.Debit,
                                        Credit = details.Credit,
                                        CreatedBy = modelHeader.CreatedBy,
                                        CreatedDate = modelHeader.CreatedDate
                                    }
                                );
                        }

                        await _dbContext.JournalBooks.AddRangeAsync(journalBook, cancellationToken);

                        #endregion --Journal Book Recording(JV)--

                        #region --Audit Trail Recording

                        if (modelHeader.OriginalSeriesNumber.IsNullOrEmpty() && modelHeader.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Posted journal voucher# {modelHeader.JournalVoucherHeaderNo}", "Journal Voucher", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Journal Voucher has been Posted.";
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

            return NotFound();
        }

        public async Task<IActionResult> Void(int id, CancellationToken cancellationToken)
        {
            var model = await _dbContext.JournalVoucherHeaders.FirstOrDefaultAsync(x => x.JournalVoucherHeaderId == id, cancellationToken);
            var findJournalVoucherInJournalBook = await _dbContext.JournalBooks.Where(jb => jb.Reference == model!.JournalVoucherHeaderNo).ToListAsync(cancellationToken);
            var findJournalVoucherInGeneralLedger = await _dbContext.GeneralLedgerBooks.Where(jb => jb.Reference == model!.JournalVoucherHeaderNo).ToListAsync(cancellationToken);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedDate : DateTime.Now;
            try
            {
                if (model != null)
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

                        if (findJournalVoucherInJournalBook.Any())
                        {
                            await _generalRepo.RemoveRecords<JournalBook>(crb => crb.Reference == model.JournalVoucherHeaderNo, cancellationToken);
                        }
                        if (findJournalVoucherInGeneralLedger.Any())
                        {
                            await _generalRepo.RemoveRecords<GeneralLedgerBook>(gl => gl.Reference == model.JournalVoucherHeaderNo, cancellationToken);
                        }

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Voided journal voucher# {model.JournalVoucherHeaderNo}", "Journal Voucher", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Journal Voucher has been Voided.";
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

        public async Task<IActionResult> Cancel(int id, string cancellationRemarks, CancellationToken cancellationToken)
        {
            var model = await _dbContext.JournalVoucherHeaders.FirstOrDefaultAsync(x => x.JournalVoucherHeaderId == id, cancellationToken);
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
                            AuditTrail auditTrailBook = new(createdBy, $"Cancelled journal voucher# {model.JournalVoucherHeaderNo}", "Journal Voucher", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Journal Voucher has been Cancelled.";
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
        public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
        {
            if (id == 0)
            {
                return NotFound();
            }
            var existingHeaderModel = await _dbContext.JournalVoucherHeaders
                .Include(jv => jv.CheckVoucherHeader)
                .FirstOrDefaultAsync(cvh => cvh.JournalVoucherHeaderId == id, cancellationToken);
            var existingDetailsModel = await _dbContext.JournalVoucherDetails
                .Where(cvd => cvd.TransactionNo == existingHeaderModel!.JournalVoucherHeaderNo)
                .ToListAsync(cancellationToken);

            if (existingHeaderModel == null || !existingDetailsModel.Any())
            {
                return NotFound();
            }

            var accountNumbers = existingDetailsModel.Select(model => model.AccountNo).ToArray();
            var accountTitles = existingDetailsModel.Select(model => model.AccountName).ToArray();
            var debit = existingDetailsModel.Select(model => model.Debit).ToArray();
            var credit = existingDetailsModel.Select(model => model.Credit).ToArray();

            JournalVoucherViewModel model = new()
            {
                JVId = existingHeaderModel.JournalVoucherHeaderId,
                JVNo = existingHeaderModel.JournalVoucherHeaderNo,
                TransactionDate = existingHeaderModel.Date,
                References = existingHeaderModel.References,
                CVId = existingHeaderModel.CVId,
                Particulars = existingHeaderModel.Particulars,
                CRNo = existingHeaderModel.CRNo,
                JVReason = existingHeaderModel.JVReason,
                AccountNumber = accountNumbers,
                AccountTitle = accountTitles,
                Debit = debit,
                Credit = credit,
                CheckVoucherHeaders = await _dbContext.CheckVoucherHeaders
                .OrderBy(c => c.CheckVoucherHeaderId)
                .Select(cvh => new SelectListItem
                {
                    Value = cvh.CheckVoucherHeaderId.ToString(),
                    Text = cvh.CheckVoucherHeaderNo
                })
                .ToListAsync(cancellationToken),
                COA = await _dbContext.ChartOfAccounts
                    .Where(coa => !new[] { "202010200", "202010100", "101010100" }.Any(excludedNumber => coa.AccountNumber!.Contains(excludedNumber)) && !coa.HasChildren)
                    .Select(s => new SelectListItem
                    {
                        Value = s.AccountNumber,
                        Text = s.AccountNumber + " " + s.AccountName
                    })
                    .ToListAsync(cancellationToken)
            };

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(JournalVoucherViewModel viewModel, IFormFile? file, CancellationToken cancellationToken)
        {
            var existingModel = await _dbContext.JournalVoucherHeaders
                .Include(jvd => jvd.Details)
                .FirstOrDefaultAsync(jvh => jvh.JournalVoucherHeaderId == viewModel.JVId, cancellationToken);
            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Saving the default entries

                    existingModel!.JournalVoucherHeaderNo = viewModel.JVNo;
                    existingModel.Date = viewModel.TransactionDate;
                    existingModel.References = viewModel.References;
                    existingModel.CVId = viewModel.CVId;
                    existingModel.Particulars = viewModel.Particulars;
                    existingModel.CRNo = viewModel.CRNo;
                    existingModel.JVReason = viewModel.JVReason;

                    #endregion --Saving the default entries

                    #region --CV Details Entry

                    // Dictionary to keep track of AccountNo and their ids for comparison
                    var accountTitleDict = new Dictionary<string, List<int>>();
                    foreach (var details in existingModel.Details)
                    {
                        if (!accountTitleDict.ContainsKey(details.AccountNo))
                        {
                            accountTitleDict[details.AccountNo] = new List<int>();
                        }
                        accountTitleDict[details.AccountNo].Add(details.JournalVoucherDetailId);
                    }

                    // Add or update records
                    for (int i = 0; i < viewModel.AccountTitle?.Length; i++)
                    {
                        var getAccountName = await _dbContext.ChartOfAccounts.FirstOrDefaultAsync(x => x.AccountNumber == viewModel.AccountNumber![i], cancellationToken);

                        if (accountTitleDict.TryGetValue(viewModel.AccountNumber?[i], out var ids))
                        {
                            // Update the first matching record and remove it from the list
                            var detailsId = ids.First();
                            ids.RemoveAt(0);
                            var details = existingModel.Details.First(o => o.JournalVoucherDetailId == detailsId);
                            var getOriginalDocumentId =
                                existingModel.Details.FirstOrDefault(x => x.AccountNo == details.AccountNo);

                            var acctNo = await _dbContext.ChartOfAccounts
                                .FirstOrDefaultAsync(x => x.AccountNumber == viewModel.AccountNumber![i], cancellationToken: cancellationToken);

                            details.AccountNo = acctNo!.AccountNumber;
                            details.AccountName = getAccountName.AccountName;
                            details.Debit = viewModel.Debit[i];
                            details.Credit = viewModel.Credit[i];
                            details.TransactionNo = existingModel.JournalVoucherHeaderNo!;
                            details.JournalVoucherHeaderId = existingModel.JournalVoucherHeaderId;
                            details.OriginalDocumentId = getOriginalDocumentId?.OriginalDocumentId;

                            if (ids.Count == 0)
                            {
                                accountTitleDict.Remove(viewModel.AccountNumber![i]);
                            }
                        }
                        else
                        {
                            var getOriginalDocumentId = existingModel.Details.ToArray();
                            // Add new record
                            var newDetails = new JournalVoucherDetail
                            {
                                AccountNo = viewModel.AccountNumber![i],
                                AccountName = getAccountName.AccountName,
                                Debit = viewModel.Debit[i],
                                Credit = viewModel.Credit[i],
                                TransactionNo = existingModel.JournalVoucherHeaderNo!,
                                JournalVoucherHeaderId = existingModel.JournalVoucherHeaderId,
                                OriginalDocumentId = getOriginalDocumentId[i].OriginalDocumentId
                            };
                            await _dbContext.JournalVoucherDetails.AddAsync(newDetails, cancellationToken);
                        }
                    }

                    // Remove remaining records that were duplicates
                    foreach (var ids in accountTitleDict.Values)
                    {
                        foreach (var id in ids)
                        {
                            var details = existingModel.Details.First(o => o.JournalVoucherDetailId == id);
                            _dbContext.JournalVoucherDetails.Remove(details);
                        }
                    }

                    #endregion --CV Details Entry

                    if (_dbContext.ChangeTracker.HasChanges())
                    {
                        #region --Audit Trail Recording

                        if (existingModel.OriginalSeriesNumber.IsNullOrEmpty() && existingModel.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Edit journal voucher# {viewModel.JVNo}", "Journal Voucher", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);  // await the SaveChangesAsync method
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Journal Voucher edited successfully";
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
                    viewModel.CheckVoucherHeaders = await _dbContext.CheckVoucherHeaders
                        .OrderBy(c => c.CheckVoucherHeaderId)
                        .Select(cvh => new SelectListItem
                        {
                            Value = cvh.CheckVoucherHeaderId.ToString(),
                            Text = cvh.CheckVoucherHeaderNo
                        })
                        .ToListAsync(cancellationToken);
                    viewModel.COA = await _dbContext.ChartOfAccounts
                        .Where(coa =>
                            !new[] { "202010200", "202010100", "101010100" }.Any(excludedNumber =>
                                coa.AccountNumber!.Contains(excludedNumber)) && !coa.HasChildren)
                        .Select(s => new SelectListItem
                        {
                            Value = s.AccountNumber,
                            Text = s.AccountNumber + " " + s.AccountName
                        })
                        .ToListAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                    return View(viewModel);
                }
            }

            TempData["error"] = "The information provided was invalid.";
            viewModel.CheckVoucherHeaders = await _dbContext.CheckVoucherHeaders
                .OrderBy(c => c.CheckVoucherHeaderId)
                .Select(cvh => new SelectListItem
                {
                    Value = cvh.CheckVoucherHeaderId.ToString(),
                    Text = cvh.CheckVoucherHeaderNo
                })
                .ToListAsync(cancellationToken);
            viewModel.COA = await _dbContext.ChartOfAccounts
                .Where(coa =>
                    !new[] { "202010200", "202010100", "101010100" }.Any(excludedNumber =>
                        coa.AccountNumber!.Contains(excludedNumber)) && !coa.HasChildren)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);
            return View(viewModel);
        }
    }
}
