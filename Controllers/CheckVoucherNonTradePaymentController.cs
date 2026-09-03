using System.Globalization;
using Accounting_System.Data;
using Accounting_System.Repository;
using Accounting_System.Models;
using Accounting_System.Models.AccountsPayable;
using Accounting_System.Models.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Linq.Dynamic.Core;
using Accounting_System.Models.Reports;
using Accounting_System.Utility;
using Microsoft.IdentityModel.Tokens;

namespace Accounting_System.Controllers
{
    public class CheckVoucherNonTradePaymentController : Controller
    {
        private readonly UserManager<IdentityUser> _userManager;

        private readonly ApplicationDbContext _dbContext;

        private readonly IWebHostEnvironment _webHostEnvironment;

        private readonly ILogger<CheckVoucherNonTradePaymentController> _logger;

        private readonly GeneralRepo _generalRepo;

        private readonly CheckVoucherRepo _checkVoucherRepo;

        public CheckVoucherNonTradePaymentController(UserManager<IdentityUser> userManager,
            ApplicationDbContext dbContext,
            IWebHostEnvironment webHostEnvironment,
            ILogger<CheckVoucherNonTradePaymentController> logger,
            GeneralRepo generalRepo,
            CheckVoucherRepo checkVoucherRepo)
        {
            _userManager = userManager;
            _dbContext = dbContext;
            _webHostEnvironment = webHostEnvironment;
            _logger = logger;
            _generalRepo = generalRepo;
            _checkVoucherRepo = checkVoucherRepo;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> GetPaymentCheckVouchers([FromForm] DataTablesParameters parameters, CancellationToken cancellationToken)
        {
            try
            {
                var checkVoucherDetails = await _checkVoucherRepo.GetCheckVouchersAsync(cancellationToken);

                // Search filter
                if (!string.IsNullOrEmpty(parameters.Search.Value))
                {
                    var searchValue = parameters.Search.Value.ToLower();

                    checkVoucherDetails = checkVoucherDetails
                        .Where(cv =>
                            cv.CheckVoucherHeaderNo!.ToLower().Contains(searchValue) ||
                            cv.Date.ToString(CS.Date_Format).ToLower().Contains(searchValue) ||
                            cv.Supplier?.SupplierName.ToLower().Contains(searchValue) == true ||
                            cv.Total.ToString(CultureInfo.InvariantCulture).Contains(searchValue) ||
                            cv.Amount?.ToString()?.Contains(searchValue) == true ||
                            cv.AmountPaid.ToString(CultureInfo.InvariantCulture).Contains(searchValue) ||
                            cv.Category.ToLower().Contains(searchValue) ||
                            cv.CvType?.ToLower().Contains(searchValue) == true ||
                            cv.CreatedBy!.ToLower().Contains(searchValue)
                        )
                    .ToList();
                }

                // Sorting
                if (parameters.Order?.Count > 0)
                {
                    var orderColumn = parameters.Order[0];
                    var columnName = parameters.Columns[orderColumn.Column].Data;
                    var sortDirection = orderColumn.Dir.ToLower() == "asc" ? "ascending" : "descending";

                    checkVoucherDetails = checkVoucherDetails
                        .AsQueryable()
                        .OrderBy($"{columnName} {sortDirection}")
                        .ToList();
                }

                var totalRecords = checkVoucherDetails.Count();

                var pagedData = checkVoucherDetails
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
                _logger.LogError(ex, "Failed to get check voucher payment. Error: {ErrorMessage}, Stack: {StackTrace}.",
                    ex.Message, ex.StackTrace);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpGet]
        public async Task<IActionResult> Print(int? id, CancellationToken cancellationToken)
        {
            if (id == null)
            {
                return NotFound();
            }

            var header = await _dbContext.CheckVoucherHeaders
                .Include(x => x.Supplier)
                .FirstOrDefaultAsync(cvh => cvh.CheckVoucherHeaderId == id.Value, cancellationToken);

            if (header == null)
            {
                return NotFound();
            }

            var details = await _dbContext.CheckVoucherDetails
                .Where(cvd => cvd.CheckVoucherHeaderId == header.CheckVoucherHeaderId)
                .ToListAsync(cancellationToken);

            var viewModel = new CheckVoucherVM
            {
                Header = header,
                Details = details
            };

            return View(viewModel);
        }

        public async Task<IActionResult> Printed(int id, int? supplierId, CancellationToken cancellationToken)
        {
            var cv = await _dbContext.CheckVoucherHeaders.FirstOrDefaultAsync(x => x.CheckVoucherHeaderId == id, cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            if (!cv!.IsPrinted)
            {
                #region --Audit Trail Recording

                if (cv.OriginalSeriesNumber.IsNullOrEmpty() && cv.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy,
                        $"Printed original copy of check voucher# {cv.CheckVoucherHeaderNo}", "Check Voucher Non Trade Payment", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                cv.IsPrinted = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return RedirectToAction(nameof(Print), new { id, supplierId });
        }

        public async Task<IActionResult> Post(int id, CancellationToken cancellationToken)
        {
            var modelHeader = await _dbContext.CheckVoucherHeaders.FirstOrDefaultAsync(cv => cv.CheckVoucherHeaderId == id, cancellationToken);
            var modelDetails = await _dbContext.CheckVoucherDetails.Where(cvd => cvd.CheckVoucherHeaderId == modelHeader!.CheckVoucherHeaderId).ToListAsync(cancellationToken: cancellationToken);

            if (modelHeader != null)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = !modelHeader.OriginalSeriesNumber.IsNullOrEmpty() && modelHeader.OriginalDocumentId != 0 ? modelHeader.PostedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                var date = !modelHeader.OriginalSeriesNumber.IsNullOrEmpty() && modelHeader.OriginalDocumentId != 0 ? modelHeader.PostedDate : DateTime.Now;
                try
                {
                    if (!modelHeader.IsPosted)
                    {
                        modelHeader.PostedBy = createdBy;
                        modelHeader.PostedDate = date;
                        modelHeader.IsPosted = true;
                        //modelHeader.Status = nameof(CheckVoucherPaymentStatus.Posted);

                        #region --General Ledger Book Recording(CV)--

                        var accountTitlesDto = await _generalRepo.GetListOfAccountTitleDto(cancellationToken);
                        var ledgers = new List<GeneralLedgerBook>();
                        foreach (var details in modelDetails)
                        {
                            var account = accountTitlesDto.Find(c => c.AccountNumber == details.AccountNo) ?? throw new ArgumentException($"Account title '{details.AccountNo}' not found.");
                            ledgers.Add(
                                    new GeneralLedgerBook
                                    {
                                        Date = modelHeader.Date,
                                        Reference = modelHeader.CheckVoucherHeaderNo!,
                                        Description = modelHeader.Particulars!,
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

                        #endregion --General Ledger Book Recording(CV)--

                        #region --Disbursement Book Recording(CV)--

                        var disbursement = new List<DisbursementBook>();
                        foreach (var details in modelDetails)
                        {
                            var bank = _dbContext.BankAccounts.FirstOrDefault(model => model.BankAccountId == modelHeader.BankId);
                            disbursement.Add(
                                    new DisbursementBook
                                    {
                                        Date = modelHeader.Date,
                                        CVNo = modelHeader.CheckVoucherHeaderNo!,
                                        Payee = modelHeader.Payee!,
                                        Amount = modelHeader.Total,
                                        Particulars = modelHeader.Particulars!,
                                        Bank = bank != null ? bank.Bank : "N/A",
                                        CheckNo = modelHeader.CheckNo!,
                                        CheckDate = modelHeader.CheckDate?.ToString("MM/dd/yyyy")!,
                                        ChartOfAccount = details.AccountNo + " " + details.AccountName,
                                        Debit = details.Debit,
                                        Credit = details.Credit,
                                        CreatedBy = modelHeader.CreatedBy,
                                        CreatedDate = modelHeader.CreatedDate
                                    }
                                );
                        }

                        await _dbContext.DisbursementBooks.AddRangeAsync(disbursement, cancellationToken);

                        #endregion --Disbursement Book Recording(CV)--

                        #region --Audit Trail Recording

                        if (modelHeader.OriginalSeriesNumber.IsNullOrEmpty() && modelHeader.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy,
                                $"Posted check voucher# {modelHeader.CheckVoucherHeaderNo}", "Check Voucher Non Trade Payment", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        var updateMultipleInvoicingVoucher = await _dbContext.MultipleCheckVoucherPayments
                            .Where(mcvp => mcvp.CheckVoucherHeaderPaymentId == id)
                            .Include(mcvp => mcvp.CheckVoucherHeaderInvoice)
                            .ToListAsync(cancellationToken);

                        foreach (var t in updateMultipleInvoicingVoucher)
                        {
                            if (t.CheckVoucherHeaderInvoice!.IsPaid)
                            {
                                t.CheckVoucherHeaderInvoice!.IsPaid = true;
                            }
                        }

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Check Voucher has been Posted.";
                    }
                    return RedirectToAction(nameof(Print), new { id });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to post check voucher. Error: {ErrorMessage}, Stack: {StackTrace}. Posted by: {UserName}",
                        ex.Message, ex.StackTrace, createdBy);
                    await transaction.RollbackAsync(cancellationToken);

                    TempData["error"] = ex.Message;
                    return RedirectToAction(nameof(Index));
                }
            }

            return NotFound();
        }

        public async Task<IActionResult> Cancel(int id, string? cancellationRemarks, CancellationToken cancellationToken)
        {
            var existingHeaderModel = await _dbContext.CheckVoucherHeaders.FirstOrDefaultAsync(x => x.CheckVoucherHeaderId == id, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !existingHeaderModel.OriginalSeriesNumber.IsNullOrEmpty() && existingHeaderModel.OriginalDocumentId != 0 ? existingHeaderModel.CanceledBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !existingHeaderModel.OriginalSeriesNumber.IsNullOrEmpty() && existingHeaderModel.OriginalDocumentId != 0 ? existingHeaderModel.CanceledDate : DateTime.Now;
            try
            {
                if (existingHeaderModel != null)
                {
                    existingHeaderModel.CanceledBy = createdBy;
                    existingHeaderModel.CanceledDate = date;
                    existingHeaderModel.IsCanceled = true;
                    //existingHeaderModel.Status = nameof(CheckVoucherPaymentStatus.Canceled);
                    existingHeaderModel.CancellationRemarks = cancellationRemarks;

                var isForTheBir = existingHeaderModel.SupplierId == await _dbContext.Suppliers
                    .Where(s => s.SupplierName.Contains("BUREAU OF INTERNAL REVENUE"))
                    .Select(s => s.SupplierId)
                    .FirstOrDefaultAsync(cancellationToken: cancellationToken); //BIR

                var getCVs = await _dbContext.MultipleCheckVoucherPayments
                    .Where(cvp => cvp.CheckVoucherHeaderPaymentId == existingHeaderModel.CheckVoucherHeaderId)
                    .Include(cvp => cvp.CheckVoucherHeaderInvoice)
                    .Include(cvp => cvp.CheckVoucherHeaderPayment)
                    .ToListAsync(cancellationToken);

                if (isForTheBir)
                {
                    foreach (var cv in getCVs)
                    {
                        var existingDetails = await _dbContext.CheckVoucherDetails
                            .Where(d => d.CheckVoucherHeaderId == cv.CheckVoucherHeaderInvoiceId &&
                                        d.SupplierId == existingHeaderModel.SupplierId)
                            .ToListAsync(cancellationToken);

                        foreach (var existingDetail in existingDetails)
                        {
                            existingDetail.AmountPaid = 0;
                        }

                    }
                }
                else
                {
                    foreach (var cv in getCVs)
                    {
                        cv.CheckVoucherHeaderInvoice!.AmountPaid -= cv.AmountPaid;
                        cv.CheckVoucherHeaderInvoice.IsPaid = false;
                    }
                }

                #region --Audit Trail Recording

                if (existingHeaderModel.OriginalSeriesNumber.IsNullOrEmpty() && existingHeaderModel.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy,
                        $"Canceled check voucher# {existingHeaderModel.CheckVoucherHeaderNo}", "Check Voucher Non Trade Payment", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                TempData["success"] = "Check Voucher has been Cancelled.";

                return RedirectToAction(nameof(Index));
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Failed to cancel check voucher. Error: {ErrorMessage}, Stack: {StackTrace}. Canceled by: {UserName}",
                    ex.Message, ex.StackTrace, createdBy);
                TempData["error"] = $"Error: '{ex.Message}'";
                return RedirectToAction(nameof(Index));
            }

            return NotFound();
        }

        public async Task<IActionResult> Void(int id, CancellationToken cancellationToken)
        {
            var existingHeaderModel = await _dbContext.CheckVoucherHeaders.FirstOrDefaultAsync(x => x.CheckVoucherHeaderId == id, cancellationToken);

            if (existingHeaderModel != null)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = !existingHeaderModel.OriginalSeriesNumber.IsNullOrEmpty() && existingHeaderModel.OriginalDocumentId != 0 ? existingHeaderModel.VoidedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                var date = !existingHeaderModel.OriginalSeriesNumber.IsNullOrEmpty() && existingHeaderModel.OriginalDocumentId != 0 ? existingHeaderModel.VoidedDate : DateTime.Now;
                try
                {
                    var isForTheBir = existingHeaderModel.SupplierId == await _dbContext.Suppliers
                        .Where(s => s.SupplierName.Contains("BUREAU OF INTERNAL REVENUE"))
                        .Select(s => s.SupplierId)
                        .FirstOrDefaultAsync(cancellationToken: cancellationToken); //BIR

                    var getCVs = await _dbContext.MultipleCheckVoucherPayments
                        .Where(cvp => cvp.CheckVoucherHeaderPaymentId == existingHeaderModel.CheckVoucherHeaderId)
                        .Include(cvp => cvp.CheckVoucherHeaderInvoice)
                        .Include(cvp => cvp.CheckVoucherHeaderPayment)
                        .ToListAsync(cancellationToken);

                    if (isForTheBir)
                    {
                        foreach (var cv in getCVs)
                        {
                            var existingDetails = await _dbContext.CheckVoucherDetails
                                .Where(d => d.CheckVoucherHeaderId == cv.CheckVoucherHeaderInvoiceId &&
                                            d.SupplierId == existingHeaderModel.SupplierId)
                                .ToListAsync(cancellationToken);

                            foreach (var existingDetail in existingDetails)
                            {
                                existingDetail.AmountPaid = 0;
                            }

                        }
                    }
                    else
                    {
                        foreach (var cv in getCVs)
                        {
                            cv.CheckVoucherHeaderInvoice!.AmountPaid -= cv.AmountPaid;
                            cv.CheckVoucherHeaderInvoice.IsPaid = false;
                            //cv.CheckVoucherHeaderInvoice.Status = nameof(CheckVoucherInvoiceStatus.ForPayment);
                        }
                    }

                    existingHeaderModel.IsPosted = false;
                    existingHeaderModel.VoidedBy = createdBy;
                    existingHeaderModel.VoidedDate = date;
                    existingHeaderModel.IsVoided = true;
                    //existingHeaderModel.Status = nameof(CheckVoucherPaymentStatus.Voided);


                    await _generalRepo.RemoveRecords<DisbursementBook>(db => db.CVNo == existingHeaderModel.CheckVoucherHeaderNo, cancellationToken);
                    await _generalRepo.RemoveRecords<GeneralLedgerBook>(gl => gl.Reference == existingHeaderModel.CheckVoucherHeaderNo, cancellationToken);

                    //re-compute amount paid in trade and payment voucher
                    #region --Audit Trail Recording

                    if (existingHeaderModel.OriginalSeriesNumber.IsNullOrEmpty() && existingHeaderModel.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy,
                            $"Voided check voucher# {existingHeaderModel.CheckVoucherHeaderNo}", "Check Voucher Non Trade Payment", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Check Voucher has been Voided.";

                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to void check voucher. Error: {ErrorMessage}, Stack: {StackTrace}. Voided by: {UserName}",
                        ex.Message, ex.StackTrace, createdBy);
                    await transaction.RollbackAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                    return RedirectToAction(nameof(Index));
                }
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

            var existingHeaderModel = await _dbContext.CheckVoucherHeaders
                .Include(cvh => cvh.Supplier)
                .FirstOrDefaultAsync(cvh => cvh.CheckVoucherHeaderId == id, cancellationToken);

            var existingDetailsModel = await _dbContext.CheckVoucherDetails
                .Where(cvd => cvd.CheckVoucherHeaderId == existingHeaderModel!.CheckVoucherHeaderId)
                .Include(cvd => cvd.Supplier)
                .FirstOrDefaultAsync(cancellationToken: cancellationToken);

            if (existingHeaderModel == null || existingDetailsModel == null)
            {
                return NotFound();
            }

            var checkVoucher = await _dbContext.CheckVoucherDetails
                .Where(cvd => cvd.CheckVoucherHeader!.SupplierId != null && cvd.CheckVoucherHeader.IsPosted && cvd.CheckVoucherHeader.CvType == nameof(CVType.Invoicing) ||
                              cvd.SupplierId != null && cvd.CheckVoucherHeader.IsPosted && cvd.CheckVoucherHeader.CvType == nameof(CVType.Invoicing) && cvd.CheckVoucherHeaderId == cvd.CheckVoucherHeader.CheckVoucherHeaderId)
                .Include(cvd => cvd.CheckVoucherHeader)
                .OrderBy(cvd => cvd.CheckVoucherDetailId)
                .Select(cvd => new SelectListItem
                {
                    Value = cvd.CheckVoucherHeaderId.ToString(),
                    Text = cvd.CheckVoucherHeader!.CheckVoucherHeaderNo
                })
                .Distinct()
                .ToListAsync(cancellationToken: cancellationToken);

            var suppliers = await _dbContext.Suppliers
                .Select(cvh => new SelectListItem
                {
                    Value = cvh.SupplierId.ToString(),
                    Text = cvh.SupplierName
                })
                .ToListAsync(cancellationToken: cancellationToken);

            var bankAccounts = await _dbContext.BankAccounts
                .Select(ba => new SelectListItem
                {
                    Value = ba.BankAccountId.ToString(),
                    Text = ba.Bank + " " + ba.AccountName
                })
                .ToListAsync(cancellationToken: cancellationToken);

            var getCVs = await _dbContext.MultipleCheckVoucherPayments
                .Where(cvp => cvp.CheckVoucherHeaderPaymentId == existingHeaderModel.CheckVoucherHeaderId)
                .Select(cvp => cvp.CheckVoucherHeaderInvoiceId)
                .ToListAsync(cancellationToken);

            //for trim the system generated invoice reference to payment
            var particulars = existingHeaderModel.Particulars ?? "";
            var index = particulars.IndexOf("Payment for", StringComparison.CurrentCultureIgnoreCase);

            CheckVoucherNonTradePaymentViewModel model = new()
            {
                TransactionDate = existingHeaderModel.Date,
                MultipleCvId = getCVs.ToArray(),
                CheckVouchers = checkVoucher,
                Total = existingHeaderModel.AmountPaid,
                BankId = existingHeaderModel.BankId ?? 0,
                Banks = bankAccounts,
                CheckNo = existingHeaderModel.CheckNo!,
                CheckDate = existingHeaderModel.CheckDate ?? default,
                Particulars = index >= 0 ? particulars.Substring(0, index).Trim() : particulars,
                Payee = existingHeaderModel.SupplierId != null ? existingHeaderModel.Supplier!.SupplierName : existingDetailsModel.Supplier!.SupplierName,
                PayeeAddress = existingHeaderModel.Supplier!.SupplierAddress,
                PayeeTin = existingHeaderModel.Supplier.SupplierTin,
                MultipleSupplierId = existingHeaderModel.SupplierId ?? existingDetailsModel.SupplierId,
                Suppliers = suppliers,
                CvId = existingHeaderModel.CheckVoucherHeaderId
            };

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(CheckVoucherNonTradePaymentViewModel viewModel, IFormFile? file, CancellationToken cancellationToken)
        {
            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region--Get Check Voucher Invoicing

                    var existingHeaderModel = await _dbContext.CheckVoucherHeaders
                        .Include(cv => cv.Supplier)
                        .FirstOrDefaultAsync(cv => cv.CheckVoucherHeaderId == viewModel.CvId, cancellationToken);

                    if (existingHeaderModel == null)
                    {
                        return NotFound();
                    }

                    var getCVs = await _dbContext.MultipleCheckVoucherPayments
                        .Where(cvp => cvp.CheckVoucherHeaderPaymentId == existingHeaderModel.CheckVoucherHeaderId)
                        .Include(cvp => cvp.CheckVoucherHeaderInvoice)
                        .Include(cvp => cvp.CheckVoucherHeaderPayment)
                        .ToListAsync(cancellationToken);

                    if (existingHeaderModel.SupplierId == await _dbContext.Suppliers
                            .Where(s => s.SupplierName.Contains("BUREAU OF INTERNAL REVENUE"))
                            .Select(s => s.SupplierId)
                            .FirstOrDefaultAsync(cancellationToken: cancellationToken))//BIR
                    {
                        foreach (var cv in getCVs)
                        {
                            var existingDetails = await _dbContext.CheckVoucherDetails
                                .Where(d => d.CheckVoucherHeaderId == cv.CheckVoucherHeaderInvoiceId &&
                                            d.SupplierId == existingHeaderModel.SupplierId)
                                .ToListAsync(cancellationToken);

                            foreach (var existingDetail in existingDetails)
                            {
                                existingDetail.AmountPaid = 0;
                            }

                        }
                    }

                    var invoicingVoucher = await _dbContext.CheckVoucherHeaders
                        .Where(cv => viewModel.MultipleCvId!.Contains(cv.CheckVoucherHeaderId))
                        .OrderBy(cv => cv.CheckVoucherHeaderId)
                        .ToListAsync(cancellationToken);

                    bool isForTheBir = false;

                    foreach (var invoice in invoicingVoucher)
                    {
                        var cv = viewModel.PaymentDetails.FirstOrDefault(c => c.CVId == invoice.CheckVoucherHeaderId);

                        var getCvDetails = await _dbContext.CheckVoucherDetails
                            .Where(i => cv!.CVId == i.CheckVoucherHeaderId &&
                                        i.SupplierId != null &&
                                        i.SupplierId == viewModel.MultipleSupplierId &&
                                        i.CheckVoucherHeader!.CvType == nameof(CVType.Invoicing))
                            .OrderBy(i => i.CheckVoucherHeaderId)
                            .FirstOrDefaultAsync(cancellationToken);

                        if (getCvDetails != null && getCvDetails.CheckVoucherHeaderId == cv!.CVId)
                        {
                            getCvDetails.AmountPaid += cv.AmountPaid;
                            isForTheBir = getCvDetails.SupplierId == await _dbContext.Suppliers
                                .Where(s => s.SupplierName.Contains("BUREAU OF INTERNAL REVENUE"))
                                .Select(s => s.SupplierId)
                                .FirstOrDefaultAsync(cancellationToken: cancellationToken) && !getCvDetails.IsUserSelected; //BIR Supplier Id
                        }
                    }

                    #endregion

                    #region -- Saving the default entries --

                    #region -- Check Voucher Header --


                    existingHeaderModel.Date = viewModel.TransactionDate;
                    existingHeaderModel.PONo = invoicingVoucher.Select(i => i.PONo).FirstOrDefault();
                    existingHeaderModel.SINo = invoicingVoucher.Select(i => i.SINo).FirstOrDefault();
                    existingHeaderModel.SupplierId = viewModel.MultipleSupplierId;
                    existingHeaderModel.Particulars = $"{viewModel.Particulars}. Payment for {string.Join(",", invoicingVoucher.Select(i => i.CheckVoucherHeaderNo))}";
                    existingHeaderModel.Total = viewModel.Total;
                    // existingHeaderModel.EditedBy = _userManager.GetUserName(this.User);
                    // existingHeaderModel.EditedDate = DateTimeHelper.GetCurrentPhilippineTime();
                    existingHeaderModel.Category = "Non-Trade";
                    existingHeaderModel.CvType = nameof(CVType.Payment);
                    existingHeaderModel.Reference = string.Join(", ", invoicingVoucher.Select(inv => inv.CheckVoucherHeaderNo));
                    existingHeaderModel.BankId = viewModel.BankId;
                    existingHeaderModel.Payee = viewModel.Payee;
                    // existingHeaderModel.Address = viewModel.PayeeAddress;
                    // existingHeaderModel.Tin = viewModel.PayeeTin;
                    existingHeaderModel.CheckNo = viewModel.CheckNo;
                    existingHeaderModel.CheckDate = viewModel.CheckDate;
                    existingHeaderModel.CheckAmount = viewModel.Total;
                    existingHeaderModel.Total = viewModel.Total;

                    await _dbContext.SaveChangesAsync(cancellationToken);

                    #endregion -- Check Voucher Header --

                    #region -- Multiple Payment Storing --

                    foreach (var cv in getCVs)
                    {
                        if (isForTheBir)
                        {
                            continue;
                        }

                        cv.CheckVoucherHeaderInvoice!.AmountPaid -= cv.AmountPaid;
                        cv.CheckVoucherHeaderInvoice.IsPaid = false;
                    }

                    _dbContext.RemoveRange(getCVs);

                    foreach (var paymentDetail in viewModel.PaymentDetails)
                    {
                        MultipleCheckVoucherPayment multipleCheckVoucherPayment = new()
                        {
                            Id = Guid.NewGuid(),
                            CheckVoucherHeaderPaymentId = existingHeaderModel.CheckVoucherHeaderId,
                            CheckVoucherHeaderInvoiceId = paymentDetail.CVId,
                            AmountPaid = paymentDetail.AmountPaid,
                        };

                        _dbContext.Add(multipleCheckVoucherPayment);
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }

                    #region--Update invoicing voucher

                    var updateMultipleInvoicingVoucher = await _dbContext.MultipleCheckVoucherPayments
                        .Where(mcvp => viewModel.MultipleCvId!.Contains(mcvp.CheckVoucherHeaderInvoiceId) && mcvp.CheckVoucherHeaderPaymentId == existingHeaderModel.CheckVoucherHeaderId)
                        .Include(mcvp => mcvp.CheckVoucherHeaderInvoice)
                        .ToListAsync(cancellationToken);

                    foreach (var payment in updateMultipleInvoicingVoucher)
                    {
                        if (isForTheBir)
                        {
                            continue;
                        }

                        payment.CheckVoucherHeaderInvoice!.AmountPaid += payment.AmountPaid;
                        if (payment.CheckVoucherHeaderInvoice?.AmountPaid >= payment.CheckVoucherHeaderInvoice?.InvoiceAmount)
                        {
                            payment.CheckVoucherHeaderInvoice.IsPaid = true;
                        }
                    }

                    #endregion

                    #endregion -- Multiple Payment Storing --

                    #region -- Check Voucher Details --

                    var existingDetailsModel = await _dbContext.CheckVoucherDetails
                        .Where(d => d.CheckVoucherHeaderId == existingHeaderModel.CheckVoucherHeaderId)
                        .ToListAsync(cancellationToken);

                    _dbContext.RemoveRange(existingDetailsModel);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    var details = new List<CheckVoucherDetail>();

                    for (int i = 0; i < viewModel.AccountTitle.Length; i++)
                    {
                        var getOriginalDocumentId =
                            existingDetailsModel.FirstOrDefault(x => x.AccountName == viewModel.AccountTitle[i]);
                        details.Add(new CheckVoucherDetail
                        {
                            AccountNo = viewModel.AccountNumber[i],
                            AccountName = viewModel.AccountTitle[i],
                            TransactionNo = existingHeaderModel.CheckVoucherHeaderNo!,
                            CheckVoucherHeaderId = existingHeaderModel.CheckVoucherHeaderId,
                            Debit = viewModel.Debit[i],
                            Credit = viewModel.Credit[i],
                            Amount = 0,
                            SupplierId = viewModel.AccountTitle[i] != "Cash in Bank" ? viewModel.MultipleSupplierId : null,
                            OriginalDocumentId = getOriginalDocumentId?.OriginalDocumentId
                        });
                    }

                    await _dbContext.CheckVoucherDetails.AddRangeAsync(details, cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    #endregion -- Check Voucher Details --

                    #endregion -- Saving the default entries --

                    #region -- Uploading file --

                    if (file?.Length > 0)
                    {
                        var uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "Supporting CV Files",
                            existingHeaderModel.CheckVoucherHeaderNo!);

                        if (!Directory.Exists(uploadsFolder))
                        {
                            Directory.CreateDirectory(uploadsFolder);
                        }

                        string fileName = Path.GetFileName(file.FileName);
                        string fileSavePath = Path.Combine(uploadsFolder, fileName);

                        await using FileStream stream = new FileStream(fileSavePath, FileMode.Create);
                        await file.CopyToAsync(stream, cancellationToken);

                        //if necessary add field to store location path
                        // model.Header.SupportingFilePath = fileSavePath
                    }

                    #endregion -- Uploading file --

                    #region --Audit Trail Recording

                    if (existingHeaderModel.OriginalSeriesNumber.IsNullOrEmpty() && existingHeaderModel.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy,
                            $"Edited check voucher# {existingHeaderModel.CheckVoucherHeaderNo}", "Check Voucher Non Trade Payment", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Check voucher payment edited successfully";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to edit check voucher. Error: {ErrorMessage}, Stack: {StackTrace}. Edited by: {UserName}",
                        ex.Message, ex.StackTrace, createdBy);
                    viewModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                        .Where(coa => !coa.HasChildren)
                        .Select(s => new SelectListItem
                        {
                            Value = s.AccountNumber,
                            Text = s.AccountNumber + " " + s.AccountName
                        })
                        .ToListAsync(cancellationToken);

                    viewModel.Banks = await _dbContext.BankAccounts
                        .Select(ba => new SelectListItem
                        {
                            Value = ba.BankAccountId.ToString(),
                            Text = ba.Bank + " " + ba.AccountName
                        })
                        .ToListAsync(cancellationToken: cancellationToken);

                    await transaction.RollbackAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                    return View(viewModel);
                }
            }

            viewModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                        .Where(coa => !coa.HasChildren)
                        .Select(s => new SelectListItem
                        {
                            Value = s.AccountNumber,
                            Text = s.AccountNumber + " " + s.AccountName
                        })
                        .ToListAsync(cancellationToken);


            viewModel.Banks = await _dbContext.BankAccounts
                .Select(ba => new SelectListItem
                {
                    Value = ba.BankAccountId.ToString(),
                    Text = ba.Bank + " " + ba.AccountName
                })
                .ToListAsync(cancellationToken: cancellationToken);

            TempData["error"] = "The information provided was invalid.";
            return View(viewModel);
        }

        [HttpGet]
        public async Task<JsonResult> GetMultipleSupplierDetails(int cvId, int suppId, CancellationToken cancellationToken)
        {
            var supplier = await _dbContext.Suppliers
                    .FirstOrDefaultAsync(x => x.SupplierId == suppId, cancellationToken);

            var credit = await _dbContext.CheckVoucherDetails
                .Where(cvd => cvd.SupplierId == suppId && cvd.CheckVoucherHeaderId == cvId)
                .Include(cvd => cvd.CheckVoucherHeader)
                .Select(cvd => new
                {
                    RemainingCredit = cvd.Credit - cvd.AmountPaid,
                    cvd.CheckVoucherHeader!.Particulars
                })
                .FirstOrDefaultAsync(cancellationToken);


            // Ensure that cv is not null before proceeding
            if (supplier == null || credit == null)
            {
                return Json(null);
            }

            return Json(new
            {
                Payee = supplier.SupplierName,
                PayeeAddress = supplier.SupplierAddress,
                PayeeTin = supplier.SupplierTin,
                credit.Particulars,
                Total = credit.RemainingCredit,
            });
        }

        [HttpGet]
        public async Task<JsonResult> GetMultipleSupplier(int cvId, CancellationToken cancellationToken)
        {
            var cv = await _dbContext.CheckVoucherHeaders
                    .Include(c => c.Supplier)
                    .FirstOrDefaultAsync(c => c.CheckVoucherHeaderId == cvId, cancellationToken);

            // Ensure that cv is not null before proceeding
            if (cv == null)
            {
                return Json(null);
            }

            // Retrieve the list of supplier IDs from the check voucher details
            var supplierIds = await _dbContext.CheckVoucherDetails
                .Where(cvd => cvd.TransactionNo == cv.CheckVoucherHeaderNo)
                .Select(cvd => cvd.SupplierId)
                .ToListAsync(cancellationToken);

            // Fetch suppliers whose IDs are in the supplierIds list
            var suppliers = await _dbContext.Suppliers
                .Where(supp => supplierIds.Contains(supp.SupplierId))
                .OrderBy(supp => supp.SupplierId)
                .Select(supp => new SelectListItem
                {
                    Value = supp.SupplierId.ToString(),
                    Text = supp.SupplierName
                })
                .ToListAsync(cancellationToken);

            return Json(new
            {
                SupplierList = suppliers
            });
        }

        [HttpGet]
        public async Task<JsonResult> MultipleSupplierDetails(int suppId, int cvId, CancellationToken cancellationToken)
        {
            var supplier = await _dbContext.Suppliers
                    .FirstOrDefaultAsync(x => x.SupplierId == suppId, cancellationToken);

            var credit = await _dbContext.CheckVoucherDetails
                .Where(cvd => cvd.SupplierId == suppId && cvd.CheckVoucherHeaderId == cvId)
                .Include(cvd => cvd.CheckVoucherHeader)
                .Select(cvd => new
                {
                    RemainingCredit = cvd.Credit - cvd.AmountPaid, cvd.CheckVoucherHeader!.Particulars
                })
                .FirstOrDefaultAsync(cancellationToken);


            // Ensure that cv is not null before proceeding
            if (supplier == null || credit == null)
            {
                return Json(null);
            }

            return Json(new
            {
                Payee = supplier.SupplierName,
                PayeeAddress = supplier.SupplierAddress,
                PayeeTin = supplier.SupplierTin,
                credit.Particulars,
                Total = credit.RemainingCredit
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetCheckVoucherInvoiceDetails(int? invoiceId, CancellationToken cancellationToken)
        {
            if (invoiceId == null)
            {
                return Json(null);
            }

            var invoice = await _dbContext.CheckVoucherHeaders
                .Include(i => i.Supplier)
                .FirstOrDefaultAsync(i => i.CheckVoucherHeaderId == invoiceId, cancellationToken);

            if (invoice != null)
            {
                return Json(new
                {
                    Payee = invoice.Supplier!.SupplierName,
                    PayeeAddress = invoice.Supplier.SupplierAddress,
                    PayeeTin = invoice.Supplier.SupplierTin,
                    invoice.Particulars,
                    Total = invoice.InvoiceAmount
                });
            }

            return Json(null);
        }

        [HttpGet]
        public async Task<IActionResult> Create(CancellationToken cancellationToken)
        {
            var viewModel = new CheckVoucherNonTradePaymentViewModel
            {
                ChartOfAccounts = await _dbContext.ChartOfAccounts
                    .Where(coa => !coa.HasChildren)
                    .Select(s => new SelectListItem
                    {
                        Value = s.AccountNumber,
                        Text = s.AccountNumber + " " + s.AccountName
                    })
                    .ToListAsync(cancellationToken),
                Suppliers = await _dbContext.Suppliers
                    .Select(cvh => new SelectListItem
                    {
                        Value = cvh.SupplierId.ToString(),
                        Text = cvh.SupplierName
                    })
                    .ToListAsync(cancellationToken: cancellationToken),
                Banks = await _dbContext.BankAccounts
                    .Select(ba => new SelectListItem
                    {
                        Value = ba.BankAccountId.ToString(),
                        Text = ba.Bank + " " + ba.AccountName
                    })
                    .ToListAsync(cancellationToken: cancellationToken)
            };

            return View(viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> Create(CheckVoucherNonTradePaymentViewModel viewModel, IFormFile? file, CancellationToken cancellationToken)
        {

            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region--Get Check Voucher Invoicing

                    bool isForTheBir = false;
                    var invoicingVoucher = await _dbContext.CheckVoucherHeaders
                        .Where(cv => viewModel.MultipleCvId!.Contains(cv.CheckVoucherHeaderId))
                        .OrderBy(cv => cv.CheckVoucherHeaderId)
                        .ToListAsync(cancellationToken);

                    foreach (var invoice in invoicingVoucher)
                    {
                        var cv = viewModel.PaymentDetails.FirstOrDefault(c => c.CVId == invoice.CheckVoucherHeaderId);

                        var getCvDetails = await _dbContext.CheckVoucherDetails
                            .Where(i => cv!.CVId == i.CheckVoucherHeaderId &&
                                        i.SupplierId != null &&
                                        i.SupplierId == viewModel.MultipleSupplierId &&
                                        i.CheckVoucherHeader!.CvType == nameof(CVType.Invoicing))
                            .OrderBy(i => i.CheckVoucherDetailId)
                            .FirstOrDefaultAsync(cancellationToken);

                        if (getCvDetails != null && getCvDetails.CheckVoucherHeaderId == cv!.CVId)
                        {
                            getCvDetails.AmountPaid += cv.AmountPaid;
                            isForTheBir = getCvDetails.SupplierId == await _dbContext.Suppliers
                                .Where(s => s.SupplierName.Contains("BUREAU OF INTERNAL REVENUE"))
                                .Select(s => s.SupplierId)
                                .FirstOrDefaultAsync(cancellationToken: cancellationToken) && !getCvDetails.IsUserSelected; //BIR Supplier Id
                        }
                    }

                    #endregion

                    #region -- Saving the default entries --

                    #region -- Check Voucher Header --

                    CheckVoucherHeader checkVoucherHeader = new()
                    {
                        CheckVoucherHeaderNo = await _checkVoucherRepo.GenerateCVNo(cancellationToken),
                        Date = viewModel.TransactionDate,
                        PONo = invoicingVoucher.Select(i => i.PONo).FirstOrDefault(),
                        SINo = invoicingVoucher.Select(i => i.SINo).FirstOrDefault(),
                        SupplierId = viewModel.MultipleSupplierId,
                        Particulars = $"{viewModel.Particulars}. Payment for {string.Join(",", invoicingVoucher.Select(i => i.CheckVoucherHeaderNo))}",
                        Total = viewModel.Total,
                        CreatedBy = createdBy,
                        Category = "Non-Trade",
                        CvType = nameof(CVType.Payment),
                        Reference = string.Join(", ", invoicingVoucher.Select(inv => inv.CheckVoucherHeaderNo)),
                        BankId = viewModel.BankId,
                        Payee = viewModel.Payee,
                        // Address = viewModel.PayeeAddress,
                        // Tin = viewModel.PayeeTin,
                        CheckNo = viewModel.CheckNo,
                        CheckDate = viewModel.CheckDate,
                        CheckAmount = viewModel.Total
                    };

                    await _dbContext.AddAsync(checkVoucherHeader, cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    #endregion -- Check Voucher Header --

                    #region -- Multiple Payment Storing --

                    foreach (var paymentDetail in viewModel.PaymentDetails)
                    {
                        MultipleCheckVoucherPayment multipleCheckVoucherPayment = new()
                        {
                            Id = Guid.NewGuid(),
                            CheckVoucherHeaderPaymentId = checkVoucherHeader.CheckVoucherHeaderId,
                            CheckVoucherHeaderInvoiceId = paymentDetail.CVId,
                            AmountPaid = paymentDetail.AmountPaid,
                        };

                        _dbContext.Add(multipleCheckVoucherPayment);
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }

                    #region--Update invoicing voucher

                    var updateMultipleInvoicingVoucher = await _dbContext.MultipleCheckVoucherPayments
                        .Where(mcvp => viewModel.MultipleCvId!.Contains(mcvp.CheckVoucherHeaderInvoiceId) && mcvp.CheckVoucherHeaderPaymentId == checkVoucherHeader.CheckVoucherHeaderId)
                        .Include(mcvp => mcvp.CheckVoucherHeaderInvoice)
                        .ToListAsync(cancellationToken);

                    foreach (var payment in updateMultipleInvoicingVoucher)
                    {
                        if (isForTheBir)
                        {
                            continue;
                        }

                        payment.CheckVoucherHeaderInvoice!.AmountPaid += payment.AmountPaid;
                        if (payment.CheckVoucherHeaderInvoice?.AmountPaid >= payment.CheckVoucherHeaderInvoice?.InvoiceAmount)
                        {
                            payment.CheckVoucherHeaderInvoice.IsPaid = true;
                        }
                    }

                    #endregion

                    #endregion -- Multiple Payment Storing --

                    #region -- Check Voucher Details --

                    List<CheckVoucherDetail> checkVoucherDetails = new();

                    for (int i = 0; i < viewModel.AccountNumber.Length; i++)
                    {
                        if (viewModel.Debit[i] != 0 || viewModel.Credit[i] != 0)
                        {
                            checkVoucherDetails.Add(new CheckVoucherDetail
                            {
                                AccountNo = viewModel.AccountNumber[i],
                                AccountName = viewModel.AccountTitle[i],
                                TransactionNo = checkVoucherHeader.CheckVoucherHeaderNo,
                                CheckVoucherHeaderId = checkVoucherHeader.CheckVoucherHeaderId,
                                Debit = viewModel.Debit[i],
                                Credit = viewModel.Credit[i],
                                Amount =  0,
                                SupplierId = viewModel.AccountTitle[i] != "Cash in Bank" ? viewModel.MultipleSupplierId : null
                            });
                        }
                    }

                    await _dbContext.CheckVoucherDetails.AddRangeAsync(checkVoucherDetails, cancellationToken);

                    #endregion -- Check Voucher Details --

                    #endregion -- Saving the default entries --

                    #region -- Uploading file --

                    if (file?.Length > 0)
                    {
                        var uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "Supporting CV Files",
                            checkVoucherHeader.CheckVoucherHeaderNo);

                        if (!Directory.Exists(uploadsFolder))
                        {
                            Directory.CreateDirectory(uploadsFolder);
                        }

                        var fileName = Path.GetFileName(file.FileName);
                        var fileSavePath = Path.Combine(uploadsFolder, fileName);

                        await using FileStream stream = new FileStream(fileSavePath, FileMode.Create);
                        await file.CopyToAsync(stream, cancellationToken);

                        //if necessary add field to store location path
                        // model.Header.SupportingFilePath = fileSavePath
                    }

                    #endregion -- Uploading file --

                    #region --Audit Trail Recording

                    if (checkVoucherHeader.OriginalSeriesNumber.IsNullOrEmpty() && checkVoucherHeader.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy,
                            $"Created new check voucher# {checkVoucherHeader.CheckVoucherHeaderNo}", "Check Voucher Non Trade Payment", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Check voucher payment created successfully";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create check voucher. Error: {ErrorMessage}, Stack: {StackTrace}. Created by: {UserName}",
                        ex.Message, ex.StackTrace, createdBy);
                    viewModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                        .Where(coa => !coa.HasChildren)
                        .Select(s => new SelectListItem
                        {
                            Value = s.AccountNumber,
                            Text = s.AccountNumber + " " + s.AccountName
                        })
                        .ToListAsync(cancellationToken);

                    viewModel.Banks = await _dbContext.BankAccounts
                        .Select(ba => new SelectListItem
                        {
                            Value = ba.BankAccountId.ToString(),
                            Text = ba.Bank + " " + ba.AccountName
                        })
                        .ToListAsync(cancellationToken: cancellationToken);

                    await transaction.RollbackAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                    return View(viewModel);
                }
            }

            viewModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                        .Where(coa => !coa.HasChildren)
                        .Select(s => new SelectListItem
                        {
                            Value = s.AccountNumber,
                            Text = s.AccountNumber + " " + s.AccountName
                        })
                        .ToListAsync(cancellationToken);


            viewModel.Banks = await _dbContext.BankAccounts
                .Select(ba => new SelectListItem
                {
                    Value = ba.BankAccountId.ToString(),
                    Text = ba.Bank + " " + ba.AccountName
                })
                .ToListAsync(cancellationToken: cancellationToken);

            TempData["error"] = "The information provided was invalid.";
            return View(viewModel);
        }

        public async Task<IActionResult> GetSupplierDetails(int? supplierId)
        {
            if (supplierId != null)
            {
                var supplier = await _dbContext.Suppliers
                    .FindAsync(supplierId);

                if (supplier != null)
                {
                    return Json(new
                    {
                        Name = supplier.SupplierName,
                        Address = supplier.SupplierAddress,
                        TinNo = supplier.SupplierTin,
                        supplier.TaxType,
                        supplier.Category,
                        TaxPercent = supplier.WithholdingTaxPercent,
                        supplier.VatType,
                        DefaultExpense = supplier.DefaultExpenseNumber,
                        WithholdingTax = supplier.WithholdingTaxtitle
                    });
                }
            }
            return Json(null);
        }

        public async Task<IActionResult> GetCVs(int supplierId, int? paymentId, CancellationToken cancellationToken)
        {
            try
            {
                var query = _dbContext.CheckVoucherDetails
                    .Include(cvd => cvd.CheckVoucherHeader)
                    .Where(cvd =>
                        cvd.CheckVoucherHeader!.IsPosted &&
                        cvd.CheckVoucherHeader.CvType == nameof(CVType.Invoicing) &&
                        (
                            (cvd.CheckVoucherHeader.SupplierId != null &&
                             cvd.CheckVoucherHeader.SupplierId == supplierId &&
                             !cvd.CheckVoucherHeader.IsPaid) ||
                            (cvd.SupplierId != null &&
                             cvd.SupplierId == supplierId &&
                             cvd.Credit > cvd.AmountPaid)
                        ));

                if (paymentId != null)
                {
                    var existingInvoiceIds = await _dbContext.MultipleCheckVoucherPayments
                        .Where(m => m.CheckVoucherHeaderPaymentId == paymentId)
                        .Select(m => m.CheckVoucherHeaderInvoiceId)
                        .ToListAsync(cancellationToken);

                    // Include existing records in the query
                    query = query.Union(_dbContext.CheckVoucherDetails
                        .Include(cvd => cvd.CheckVoucherHeader)
                        .Where(cvd => cvd.SupplierId == supplierId && existingInvoiceIds.Contains(cvd.CheckVoucherHeaderId ?? 0)));
                }

                var checkVouchers = await query.ToListAsync(cancellationToken);

                if (!checkVouchers.Any())
                {
                    return Json(null);
                }

                var cvList = checkVouchers
                    .OrderBy(cv => cv.CheckVoucherDetailId)
                    .Select(cv => new {
                        Id = cv.CheckVoucherHeader!.CheckVoucherHeaderId,
                        CVNumber = cv.CheckVoucherHeader.CheckVoucherHeaderNo
                    })
                    .Distinct()
                    .ToList();

                return Json(cvList);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get check voucher. Error: {ErrorMessage}, Stack: {StackTrace}.",
                    ex.Message, ex.StackTrace);
                return StatusCode(500, "An error occurred while processing your request.");
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetMultipleInvoiceDetails(int?[] cvId, int supplierId, CancellationToken cancellationToken)
        {
            if (!cvId.Any())
            {
                return Json(null);
            }

            var invoices = await _dbContext.CheckVoucherDetails
                .Where(i =>
                    cvId.Contains(i.CheckVoucherHeaderId) &&
                    i.SupplierId == supplierId)
                .Include(i => i.Supplier)
                .Include(i => i.CheckVoucherHeader)
                .ToListAsync(cancellationToken);

            // Get the first CV's particulars
            var firstParticulars = invoices.FirstOrDefault()?.CheckVoucherHeader?.Particulars ?? "";

            var journalEntries = new List<object>();
            var totalDebit = 0m;
            var cvBalances = new List<object>();

            var groupedInvoices = invoices.GroupBy(i => i.AccountNo);

            foreach (var invoice in invoices)
            {
                cvBalances.Add(new
                {
                    CvId = invoice.CheckVoucherHeader!.CheckVoucherHeaderId,
                    CvNumber = invoice.TransactionNo,
                    Balance = invoice.Credit,
                });
            }


            foreach (var invoice in groupedInvoices)
            {
                var balance = invoice.Sum(i => i.Credit);
                journalEntries.Add(new
                {
                    AccountNumber = invoice.First().AccountNo,
                    AccountTitle = invoice.First().AccountName,
                    Debit = balance,
                    Credit = 0m
                });
                totalDebit += balance;
            }

            // Add the "Cash in Bank" entry
            journalEntries.Add(new
            {
                AccountNumber = "101010100",
                AccountTitle = "Cash in Bank",
                Debit = 0m,
                Credit = totalDebit
            });

            return Json(new
            {
                JournalEntries = journalEntries,
                TotalDebit = totalDebit,
                TotalCredit = totalDebit,
                Particulars = firstParticulars,
                CvBalances = cvBalances
            });
        }
    }
}
