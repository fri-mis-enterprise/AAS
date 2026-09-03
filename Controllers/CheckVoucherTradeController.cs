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
using OfficeOpenXml;
using System.Linq.Dynamic.Core;
using Accounting_System.Models.Reports;
using Accounting_System.Utility;
using Microsoft.IdentityModel.Tokens;

namespace Accounting_System.Controllers
{
    public class CheckVoucherTradeController : Controller
    {
        private readonly ApplicationDbContext _dbContext;

        private readonly UserManager<IdentityUser> _userManager;

        private readonly IWebHostEnvironment _webHostEnvironment;

        private readonly ILogger<CheckVoucherTradeController> _logger;

        private readonly GeneralRepo _generalRepo;

        private readonly CheckVoucherRepo _checkVoucherRepo;

        private readonly ReceivingReportRepo _receivingReportRepo;

        private readonly PurchaseOrderRepo _purchaseOrderRepo;

        public CheckVoucherTradeController(UserManager<IdentityUser> userManager,
            ApplicationDbContext dbContext,
            IWebHostEnvironment webHostEnvironment,
            ILogger<CheckVoucherTradeController> logger,
            GeneralRepo generalRepo,
            CheckVoucherRepo checkVoucherRepo,
            PurchaseOrderRepo purchaseOrderRepo,
            ReceivingReportRepo receivingReportRepo)
        {
            _userManager = userManager;
            _dbContext = dbContext;
            _webHostEnvironment = webHostEnvironment;
            _logger = logger;
            _generalRepo = generalRepo;
            _checkVoucherRepo = checkVoucherRepo;
            _purchaseOrderRepo = purchaseOrderRepo;
            _receivingReportRepo = receivingReportRepo;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> GetCheckVouchers([FromForm] DataTablesParameters parameters, CancellationToken cancellationToken)
        {
            try
            {
                var checkVoucherHeaders = await _checkVoucherRepo.GetCheckVouchersAsync(cancellationToken);

                // Search filter
                if (!string.IsNullOrEmpty(parameters.Search.Value))
                {
                    var searchValue = parameters.Search.Value.ToLower();

                    checkVoucherHeaders = checkVoucherHeaders
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

                    checkVoucherHeaders = checkVoucherHeaders
                        .AsQueryable()
                        .OrderBy($"{columnName} {sortDirection}")
                        .ToList();
                }

                var totalRecords = checkVoucherHeaders.Count();

                var pagedData = checkVoucherHeaders
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
                _logger.LogError(ex, "Failed to get check vouchers. Error: {ErrorMessage}, Stack: {StackTrace}.",
                    ex.Message, ex.StackTrace);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpGet]
        public async Task<IActionResult> Create(CancellationToken cancellationToken)
        {
            CheckVoucherTradeViewModel model = new()
            {
                COA = await _dbContext.ChartOfAccounts
                    .Where(coa => !new[] { "202010200", "202010100", "101010100" }.Any(excludedNumber => coa.AccountNumber != null && coa.AccountNumber.Contains(excludedNumber)) && !coa.HasChildren)
                    .Select(s => new SelectListItem
                    {
                        Value = s.AccountNumber,
                        Text = s.AccountNumber + " " + s.AccountName
                    })
                    .ToListAsync(cancellationToken),
                Suppliers = await _dbContext.Suppliers
                    .Where(supp => supp.Category == "Trade")
                    .OrderBy(supp => supp.Number)
                    .Select(sup => new SelectListItem
                    {
                        Value = sup.SupplierId.ToString(),
                        Text = sup.SupplierName
                    })
                    .ToListAsync(cancellationToken: cancellationToken),
                BankAccounts = await _dbContext.BankAccounts
                    .Select(ba => new SelectListItem
                    {
                        Value = ba.BankAccountId.ToString(),
                        Text = ba.Bank + " " + ba.AccountName
                    })
                    .ToListAsync(cancellationToken: cancellationToken)
            };

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> Create(CheckVoucherTradeViewModel viewModel, IFormFile? file, CancellationToken cancellationToken)
        {
            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Check if duplicate record

                    if (!viewModel.CheckNo.Any() && !viewModel.CheckNo.Contains("DM"))
                    {
                        var cv = await _dbContext
                        .CheckVoucherHeaders
                        .Where(cv => cv.CheckNo == viewModel.CheckNo && cv.BankId == viewModel.BankId)
                        .ToListAsync(cancellationToken);
                        if (cv.Any())
                        {
                            viewModel.COA = await _dbContext.ChartOfAccounts
                                .Where(coa => !new[] { "202010200", "202010100", "101010100" }.Any(excludedNumber => coa.AccountNumber != null && coa.AccountNumber.Contains(excludedNumber)) && !coa.HasChildren)
                                .Select(s => new SelectListItem
                                {
                                    Value = s.AccountNumber,
                                    Text = s.AccountNumber + " " + s.AccountName
                                })
                                .ToListAsync(cancellationToken);

                            viewModel.Suppliers = await _dbContext.Suppliers
                                .Where(supp => supp.Category == "Trade")
                                .Select(sup => new SelectListItem
                                {
                                    Value = sup.SupplierId.ToString(),
                                    Text = sup.SupplierName
                                })
                                .ToListAsync(cancellationToken: cancellationToken);

                            viewModel.PONo = await _dbContext.PurchaseOrders
                                .Where(po => po.SupplierId == viewModel.SupplierId && po.IsPosted)
                                .Select(po => new SelectListItem
                                {
                                    Value = po.PurchaseOrderNo!.ToString(),
                                    Text = po.PurchaseOrderNo
                                })
                                .ToListAsync(cancellationToken);

                            viewModel.BankAccounts = await _dbContext.BankAccounts
                                .Select(ba => new SelectListItem
                                {
                                    Value = ba.BankAccountId.ToString(),
                                    Text = ba.Bank + " " + ba.AccountName
                                })
                                .ToListAsync(cancellationToken: cancellationToken);

                            TempData["error"] = "Check No. Is already exist";
                            return View(viewModel);
                        }
                    }

                    #endregion --Check if duplicate record

                    #region --Retrieve Supplier

                    await _dbContext
                        .Suppliers
                        .FirstOrDefaultAsync(po => po.SupplierId == viewModel.SupplierId, cancellationToken);

                    #endregion --Retrieve Supplier

                    #region -- Get PO --

                    await _dbContext.PurchaseOrders
                        .Where(po => viewModel.POSeries != null && viewModel.POSeries.Contains(po.PurchaseOrderNo))
                        .FirstOrDefaultAsync(cancellationToken: cancellationToken);

                    #endregion -- Get PO --

                    #region --Saving the default entries

                    var generateCvNo = await _checkVoucherRepo.GenerateCVNo(cancellationToken);
                    var cashInBank = viewModel.Credit[1];
                    var cvh = new CheckVoucherHeader
                    {
                        CheckVoucherHeaderNo = generateCvNo,
                        Date = viewModel.TransactionDate,
                        PONo = viewModel.POSeries,
                        SupplierId = viewModel.SupplierId,
                        Particulars = viewModel.Particulars,
                        BankId = viewModel.BankId,
                        CheckNo = viewModel.CheckNo,
                        Category = "Trade",
                        Payee = viewModel.Payee,
                        CheckDate = viewModel.CheckDate,
                        Total = cashInBank,
                        CreatedBy = createdBy,
                        CvType = "Supplier",
                        // Address = supplier.SupplierAddress,
                        // Tin = supplier.SupplierTin,
                    };

                    await _dbContext.CheckVoucherHeaders.AddAsync(cvh, cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    #endregion --Saving the default entries

                    #region --CV Details Entry

                    var cvDetails = new List<CheckVoucherDetail>();
                    for (int i = 0; i < viewModel.AccountNumber.Length; i++)
                    {
                        if (viewModel.Debit[i] != 0 || viewModel.Credit[i] != 0)
                        {
                            cvDetails.Add(
                            new CheckVoucherDetail
                            {
                                AccountNo = viewModel.AccountNumber[i],
                                AccountName = viewModel.AccountTitle[i],
                                Debit = viewModel.Debit[i],
                                Credit = viewModel.Credit[i],
                                TransactionNo = cvh.CheckVoucherHeaderNo,
                                CheckVoucherHeaderId = cvh.CheckVoucherHeaderId,
                                SupplierId = i == 0 ? viewModel.SupplierId : null
                            });
                        }
                    }

                    await _dbContext.CheckVoucherDetails.AddRangeAsync(cvDetails, cancellationToken);

                    #endregion --CV Details Entry

                    #region -- Partial payment of RR's

                    var cvTradePaymentModel = new List<CVTradePayment>();
                    foreach (var item in viewModel.RRs)
                    {
                        var getReceivingReport = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == item.Id, cancellationToken);
                        getReceivingReport!.AmountPaid += item.Amount;

                        cvTradePaymentModel.Add(
                            new CVTradePayment
                            {
                                DocumentId = getReceivingReport.ReceivingReportId,
                                DocumentType = "RR",
                                CheckVoucherId = cvh.CheckVoucherHeaderId,
                                AmountPaid = item.Amount
                            });
                    }

                    await _dbContext.AddRangeAsync(cvTradePaymentModel, cancellationToken);

                    #endregion -- Partial payment of RR's

                    #region -- Uploading file --

                    if (file?.Length > 0)
                    {
                        var uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "Supporting CV Files",
                            cvh.CheckVoucherHeaderNo);

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

                    #region --Audit Trail Recording

                    if (cvh.OriginalSeriesNumber.IsNullOrEmpty() && cvh.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy,
                            $"Create new check voucher# {cvh.CheckVoucherHeaderNo}", "Check Voucher Trade", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    TempData["success"] = "Check voucher trade created successfully";
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return RedirectToAction(nameof(Index));

                    #endregion -- Uploading file --
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create check voucher. Error: {ErrorMessage}, Stack: {StackTrace}. Created by: {UserName}",
                        ex.Message, ex.StackTrace, createdBy);
                    viewModel.COA = await _dbContext.ChartOfAccounts
                        .Where(coa => !new[] { "202010200", "202010100", "101010100" }.Any(excludedNumber => coa.AccountNumber != null && coa.AccountNumber.Contains(excludedNumber)) && !coa.HasChildren)
                        .Select(s => new SelectListItem
                        {
                            Value = s.AccountNumber,
                            Text = s.AccountNumber + " " + s.AccountName
                        })
                        .ToListAsync(cancellationToken);

                    viewModel.Suppliers = await _dbContext.Suppliers
                            .Where(supp => supp.Category == "Trade")
                            .Select(sup => new SelectListItem
                            {
                                Value = sup.SupplierId.ToString(),
                                Text = sup.SupplierName
                            })
                            .ToListAsync(cancellationToken: cancellationToken);

                    viewModel.PONo = await _dbContext.PurchaseOrders
                                .Where(po => po.SupplierId == viewModel.SupplierId && po.IsPosted)
                                .Select(po => new SelectListItem
                                {
                                    Value = po.PurchaseOrderNo!.ToString(),
                                    Text = po.PurchaseOrderNo
                                })
                                .ToListAsync(cancellationToken);

                    viewModel.BankAccounts = await _dbContext.BankAccounts
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
            viewModel.COA = await _dbContext.ChartOfAccounts
                .Where(coa => !new[] { "202010200", "202010100", "101010100" }.Any(excludedNumber => coa.AccountNumber != null && coa.AccountNumber.Contains(excludedNumber)) && !coa.HasChildren)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);

            viewModel.Suppliers = await _dbContext.Suppliers
                .Where(supp => supp.Category == "Trade")
                .Select(sup => new SelectListItem
                {
                    Value = sup.SupplierId.ToString(),
                    Text = sup.SupplierName
                })
                .ToListAsync(cancellationToken: cancellationToken);

            viewModel.PONo = await _dbContext.PurchaseOrders
                .Where(po => po.SupplierId == viewModel.SupplierId && po.IsPosted)
                .Select(po => new SelectListItem
                {
                    Value = po.PurchaseOrderNo!.ToString(),
                    Text = po.PurchaseOrderNo
                })
                .ToListAsync(cancellationToken);

            viewModel.BankAccounts = await _dbContext.BankAccounts
                .Select(ba => new SelectListItem
                {
                    Value = ba.BankAccountId.ToString(),
                    Text = ba.Bank + " " + ba.AccountName
                })
                .ToListAsync(cancellationToken: cancellationToken);

            TempData["error"] = "The information provided was invalid.";
            return View(viewModel);
        }

        public async Task<IActionResult> GetPOs(int supplierId)
        {
            var purchaseOrders = await _dbContext.PurchaseOrders
                .Where(po => po.SupplierId == supplierId && po.IsPosted)
                .ToListAsync();

            if (purchaseOrders.Any())
            {
                var poList = purchaseOrders.OrderBy(po => po.PurchaseOrderNo)
                                        .Select(po => new { Id = po.PurchaseOrderId, PONumber = po.PurchaseOrderNo })
                                        .ToList();
                return Json(poList);
            }

            return Json(null);
        }

        public async Task<IActionResult> GetRRs(string[] poNumber, int? cvId, CancellationToken cancellationToken)
        {
            var query = _dbContext.ReceivingReports
                .Where(rr => !rr.IsPaid && rr.AmountPaid == 0 && poNumber.Contains(rr.PONo) && rr.PostedBy != null);

            if (cvId != null)
            {
                var rrIds = await _dbContext.CVTradePayments
                    .Where(cvp => cvp.CheckVoucherId == cvId && cvp.DocumentType == "RR")
                    .Select(cvp => cvp.DocumentId)
                    .ToListAsync(cancellationToken);

                query = query.Union(_dbContext.ReceivingReports
                    .Where(rr => poNumber.Contains(rr.PONo) && rrIds.Contains(rr.ReceivingReportId)));
            }

            var receivingReports = await query
                .Include(rr => rr.PurchaseOrder)
                .ThenInclude(rr => rr!.Supplier)
                .OrderBy(rr => rr.PurchaseOrder!.PurchaseOrderNo)
                .ToListAsync(cancellationToken);

            if (!receivingReports.Any())
            {
                return Json(null);
            }

            var rrList = receivingReports
                .Select(rr =>
                {
                    var netOfVatAmount = rr.PurchaseOrder?.Supplier?.VatType == CS.VatType_Vatable
                        ? _generalRepo.ComputeNetOfVat(rr.Amount)
                        : rr.Amount;

                    var ewtAmount = rr.PurchaseOrder?.Supplier?.TaxType == CS.TaxType_WithTax
                        ? _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.01m)
                        : 0.0000m;

                    var netOfEwtAmount = rr.PurchaseOrder?.Supplier?.TaxType == CS.TaxType_WithTax
                        ? _generalRepo.ComputeNetOfEwt(rr.Amount, ewtAmount)
                        : rr.Amount;

                    return new
                    {
                        Id = rr.ReceivingReportId,
                        rr.ReceivingReportNo,
                        rr.PurchaseOrder?.PurchaseOrderNo,
                        AmountPaid = rr.AmountPaid.ToString(CS.Four_Decimal_Format),
                        NetOfEwtAmount = netOfEwtAmount.ToString(CS.Four_Decimal_Format)
                    };
                }).ToList();

            return Json(rrList);
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

        [HttpGet]
        public async Task<IActionResult> Edit(int? id, CancellationToken cancellationToken)
        {
            if (id == null)
            {
                return NotFound();
            }

            var existingHeaderModel = await _dbContext.CheckVoucherHeaders
                .FirstOrDefaultAsync(cvh => cvh.CheckVoucherHeaderId == id, cancellationToken);

            var existingDetailsModel = await _dbContext.CheckVoucherDetails
                .Where(cvd => cvd.CheckVoucherHeaderId == existingHeaderModel!.CheckVoucherHeaderId)
                .ToListAsync(cancellationToken);

            if (existingHeaderModel == null || !existingDetailsModel.Any())
            {
                return NotFound();
            }
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);

            CheckVoucherTradeViewModel model = new()
            {
                SupplierId = existingHeaderModel.SupplierId ?? 0,
                Payee = existingHeaderModel.Payee!,
                // SupplierAddress = existingHeaderModel.Address,
                // SupplierTinNo = existingHeaderModel.Tin,
                POSeries = existingHeaderModel.PONo,
                TransactionDate = existingHeaderModel.Date,
                BankId = existingHeaderModel.BankId,
                CheckNo = existingHeaderModel.CheckNo!,
                CheckDate = existingHeaderModel.CheckDate ?? DateOnly.MinValue,
                Particulars = existingHeaderModel.Particulars!,
                CVId = existingHeaderModel.CheckVoucherHeaderId,
                CVNo = existingHeaderModel.CheckVoucherHeaderNo,
                CreatedBy = createdBy,
                RRs = new List<ReceivingReportList>(),
                Suppliers = await _dbContext.Suppliers
                    .Where(supp => supp.Category == "Trade")
                    .OrderBy(supp => supp.Number)
                    .Select(sup => new SelectListItem
                    {
                        Value = sup.SupplierId.ToString(),
                        Text = sup.SupplierName
                    })
                    .ToListAsync(cancellationToken: cancellationToken)
            };

            var getCheckVoucherTradePayment = await _dbContext.CVTradePayments
                .Where(cv => cv.CheckVoucherId == id && cv.DocumentType == "RR")
                .ToListAsync(cancellationToken);

            foreach (var item in getCheckVoucherTradePayment)
            {
                model.RRs.Add(new ReceivingReportList
                {
                    Id = item.DocumentId,
                    Amount = item.AmountPaid
                });
            }

            model.COA = await _dbContext.ChartOfAccounts
                .Where(coa => !new[] { "202010200", "202010100", "101010100" }.Any(excludedNumber => coa.AccountNumber != null && coa.AccountNumber.Contains(excludedNumber)) && !coa.HasChildren)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);

            model.PONo = await _dbContext.PurchaseOrders
                .OrderBy(s => s.PurchaseOrderNo)
                .Select(s => new SelectListItem
                {
                    Value = s.PurchaseOrderNo,
                    Text = s.PurchaseOrderNo
                })
                .ToListAsync(cancellationToken);

            model.BankAccounts = await _dbContext.BankAccounts
                .Select(ba => new SelectListItem
                {
                    Value = ba.BankAccountId.ToString(),
                    Text = ba.Bank + " " + ba.AccountName
                })
                .ToListAsync(cancellationToken: cancellationToken);

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(CheckVoucherTradeViewModel viewModel, IFormFile? file, CancellationToken cancellationToken)
        {
            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var existingHeaderModel = await _dbContext.CheckVoucherHeaders.FirstOrDefaultAsync(cv => cv.CheckVoucherHeaderId == viewModel.CVId, cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --CV Details Entry

                    var existingDetailsModel = await _dbContext.CheckVoucherDetails
                        .Where(d => d.CheckVoucherHeaderId == existingHeaderModel!.CheckVoucherHeaderId)
                        .ToListAsync(cancellationToken: cancellationToken);

                    _dbContext.RemoveRange(existingDetailsModel);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    var details = new List<CheckVoucherDetail>();

                    var cashInBank = 0m;
                    for (int i = 0; i < viewModel.AccountTitle.Length; i++)
                    {
                        cashInBank = viewModel.Credit[1];
                        var getOriginalDocumentId =
                            existingDetailsModel.FirstOrDefault(x => x.AccountName == viewModel.AccountTitle[i]);

                        details.Add(new CheckVoucherDetail
                        {
                            AccountNo = viewModel.AccountNumber[i],
                            AccountName = viewModel.AccountTitle[i],
                            Debit = viewModel.Debit[i],
                            Credit = viewModel.Credit[i],
                            TransactionNo = existingHeaderModel!.CheckVoucherHeaderNo!,
                            CheckVoucherHeaderId = viewModel.CVId,
                            SupplierId = i == 0 ? viewModel.SupplierId : null,
                            OriginalDocumentId = getOriginalDocumentId?.OriginalDocumentId
                        });
                    }

                    await _dbContext.CheckVoucherDetails.AddRangeAsync(details, cancellationToken);

                    #endregion --CV Details Entry

                    #region --Saving the default entries

                    existingHeaderModel!.Date = viewModel.TransactionDate;
                    existingHeaderModel.PONo = viewModel.POSeries;
                    existingHeaderModel.SupplierId = viewModel.SupplierId;
                    // existingHeaderModel.Address = viewModel.SupplierAddress;
                    // existingHeaderModel.Tin = viewModel.SupplierTinNo;
                    existingHeaderModel.Particulars = viewModel.Particulars;
                    existingHeaderModel.BankId = viewModel.BankId;
                    existingHeaderModel.CheckNo = viewModel.CheckNo;
                    existingHeaderModel.Category = "Trade";
                    existingHeaderModel.Payee = viewModel.Payee;
                    existingHeaderModel.CheckDate = viewModel.CheckDate;
                    existingHeaderModel.Total = cashInBank;
                    // existingHeaderModel.EditedBy = _userManager.GetUserName(User);
                    // existingHeaderModel.EditedDate = DateTimeHelper.GetCurrentPhilippineTime();

                    #endregion --Saving the default entries

                    #region -- Partial payment of RR's

                    var getCheckVoucherTradePayment = await _dbContext.CVTradePayments
                        .Where(cv => cv.CheckVoucherId == existingHeaderModel.CheckVoucherHeaderId && cv.DocumentType == "RR")
                        .ToListAsync(cancellationToken);

                    foreach (var item in getCheckVoucherTradePayment)
                    {
                        var recevingReport = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == item.DocumentId, cancellationToken);

                        recevingReport!.AmountPaid -= item.AmountPaid;
                    }

                    _dbContext.RemoveRange(getCheckVoucherTradePayment);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    var cvTradePaymentModel = new List<CVTradePayment>();
                    foreach (var item in viewModel.RRs)
                    {
                        var getReceivingReport = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == item.Id, cancellationToken);
                        getReceivingReport!.AmountPaid += item.Amount;

                        cvTradePaymentModel.Add(
                            new CVTradePayment
                            {
                                DocumentId = getReceivingReport.ReceivingReportId,
                                DocumentType = "RR",
                                CheckVoucherId = existingHeaderModel.CheckVoucherHeaderId,
                                AmountPaid = item.Amount
                            });
                    }

                    await _dbContext.AddRangeAsync(cvTradePaymentModel, cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    #endregion -- Partial payment of RR's

                    #region -- Uploading file --

                    if (file?.Length > 0)
                    {
                        var uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "Supporting CV Files",
                            existingHeaderModel.CheckVoucherHeaderNo!);

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

                    if (existingHeaderModel.OriginalSeriesNumber.IsNullOrEmpty() && existingHeaderModel.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy,
                            $"Edited check voucher# {existingHeaderModel.CheckVoucherHeaderNo}", "Check Voucher Trade", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);  // await the SaveChangesAsync method
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Trade edited successfully";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to edit check voucher. Error: {ErrorMessage}, Stack: {StackTrace}. Edited by: {UserName}",
                        ex.Message, ex.StackTrace, createdBy);
                    viewModel.COA = await _dbContext.ChartOfAccounts
                        .Where(coa => !new[] { "202010200", "202010100", "101010100" }.Any(excludedNumber => coa.AccountNumber != null && coa.AccountNumber.Contains(excludedNumber)) && !coa.HasChildren)
                        .Select(s => new SelectListItem
                        {
                            Value = s.AccountNumber,
                            Text = s.AccountNumber + " " + s.AccountName
                        })
                        .ToListAsync(cancellationToken);

                    viewModel.PONo = await _dbContext.PurchaseOrders
                        .OrderBy(s => s.PurchaseOrderNo)
                        .Select(s => new SelectListItem
                        {
                            Value = s.PurchaseOrderNo,
                            Text = s.PurchaseOrderNo
                        })
                        .ToListAsync(cancellationToken);

                    viewModel.BankAccounts = await _dbContext.BankAccounts
                        .Select(ba => new SelectListItem
                        {
                            Value = ba.BankAccountId.ToString(),
                            Text = ba.Bank + " " + ba.AccountName
                        })
                        .ToListAsync(cancellationToken: cancellationToken);

                    viewModel.Suppliers = await _dbContext.Suppliers
                            .OrderBy(s => s.Number)
                            .Select(s => new SelectListItem
                            {
                                Value = s.SupplierId.ToString(),
                                Text = s.Number + " " + s.SupplierName
                            })
                            .ToListAsync(cancellationToken);

                    await transaction.RollbackAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                    return View(viewModel);
                }
            }

            TempData["error"] = "The information provided was invalid.";
            return View(viewModel);
        }

        [HttpGet]
        public async Task<IActionResult> Print(int? id, int? supplierId, CancellationToken cancellationToken)
        {
            if (id == null)
            {
                return NotFound();
            }

            var header = await _dbContext.CheckVoucherHeaders
                .Include(cvh => cvh.Supplier)
                .FirstOrDefaultAsync(cvh => cvh.CheckVoucherHeaderId == id.Value, cancellationToken);

            if (header == null)
            {
                return NotFound();
            }

            var details = await _dbContext.CheckVoucherDetails
                .Include(cvd => cvd.Supplier)
                .Where(cvd => cvd.CheckVoucherHeaderId == header.CheckVoucherHeaderId)
                .ToListAsync(cancellationToken);

            var getSupplier = await _dbContext.Suppliers
                .FirstOrDefaultAsync(x => x.SupplierId == supplierId, cancellationToken);

            if (header.Category == "Trade" && header.RRNo != null)
            {
                var siArray = new string[header.RRNo.Length];
                for (int i = 0; i < header.RRNo.Length; i++)
                {
                    var rrValue = header.RRNo[i];

                    var rr = await _dbContext.ReceivingReports
                                .FirstOrDefaultAsync(p => p.ReceivingReportNo == rrValue, cancellationToken: cancellationToken);

                    if (rr != null)
                    {
                        siArray[i] = rr.SupplierInvoiceNumber!;
                    }
                }

                ViewBag.SINoArray = siArray;
            }

            var viewModel = new CheckVoucherVM
            {
                Header = header,
                Details = details,
                Supplier = getSupplier
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
                        $"Printed original copy of check voucher# {cv.CheckVoucherHeaderNo}", "Check Voucher Trade", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                cv.IsPrinted = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return RedirectToAction(nameof(Print), new { id, supplierId });
        }

        public async Task<IActionResult> Post(int id, int? supplierId, CancellationToken cancellationToken)
        {
            var modelHeader = await _dbContext.CheckVoucherHeaders.FirstOrDefaultAsync(cv => cv.CheckVoucherHeaderId == id, cancellationToken);
            var modelDetails = await _dbContext.CheckVoucherDetails.Where(cvd => cvd.CheckVoucherHeaderId == modelHeader!.CheckVoucherHeaderId).ToListAsync(cancellationToken: cancellationToken);
            var supplierName = await _dbContext.Suppliers.Where(s => s.SupplierId == supplierId).Select(s => s.SupplierName).FirstOrDefaultAsync(cancellationToken);

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
                        //modelHeader.Status = nameof(Status.Posted);

                        #region -- Recalculate payment of RR's or DR's

                        var getCheckVoucherTradePayment = await _dbContext.CVTradePayments
                            .Where(cv => cv.CheckVoucherId == id)
                            .Include(cv => cv.CV)
                            .ToListAsync(cancellationToken);

                        foreach (var item in getCheckVoucherTradePayment)
                        {
                            if (item.DocumentType == "RR")
                            {
                                var receivingReport = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == item.DocumentId, cancellationToken);

                                receivingReport!.IsPaid = true;
                                receivingReport.PaidDate = DateTime.Now;
                            }
                        }

                        #endregion -- Recalculate payment of RR's or DR's

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
                                        CreatedDate = modelHeader.CreatedDate,
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
                                        Payee = modelHeader.Payee ?? supplierName!,
                                        Amount = modelHeader.Total,
                                        Particulars = modelHeader.Particulars!,
                                        Bank = bank != null ? bank.Bank : "N/A",
                                        CheckNo = !string.IsNullOrEmpty(modelHeader.CheckNo) ? modelHeader.CheckNo : "N/A",
                                        CheckDate = modelHeader.CheckDate != null ? modelHeader.CheckDate?.ToString("MM/dd/yyyy")! : "N/A",
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
                                $"Posted check voucher# {modelHeader.CheckVoucherHeaderNo}", "Check Voucher Trade", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Check Voucher has been Posted.";
                    }
                    return RedirectToAction(nameof(Print), new { id, supplierId });
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
            var model = await _dbContext.CheckVoucherHeaders.FirstOrDefaultAsync(x => x.CheckVoucherHeaderId == id, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.CanceledBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.CanceledDate : DateTime.Now;
            try
            {
                if (model != null)
                {
                    if (!model.IsCanceled)
                    {
                        model.CanceledBy = createdBy;
                        model.CanceledDate = date;
                        model.IsCanceled = true;
                        //model.Status = nameof(Status.Canceled);
                        model.CancellationRemarks = cancellationRemarks;

                        #region -- Recalculate payment of RR's or DR's

                        var getCheckVoucherTradePayment = await _dbContext.CVTradePayments
                            .Where(cv => cv.CheckVoucherId == id)
                            .Include(cv => cv.CV)
                            .ToListAsync(cancellationToken);

                        foreach (var item in getCheckVoucherTradePayment)
                        {
                            if (item.DocumentType == "RR")
                            {
                                var receivingReport = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == item.DocumentId, cancellationToken);

                                receivingReport!.IsPaid = false;
                                receivingReport.AmountPaid -= item.AmountPaid;
                            }
                        }

                        #endregion -- Recalculate payment of RR's or DR's

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy,
                                $"Canceled check voucher# {model.CheckVoucherHeaderNo}", "Check Voucher Trade", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);

                        TempData["success"] = "Check Voucher has been Cancelled.";
                    }

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
            var model = await _dbContext.CheckVoucherHeaders.FirstOrDefaultAsync(x => x.CheckVoucherHeaderId == id, cancellationToken);

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

                        model.VoidedBy = createdBy;
                        model.VoidedDate = date;
                        model.IsVoided = true;
                        //model.Status = nameof(Status.Voided);

                        await _generalRepo.RemoveRecords<DisbursementBook>(db => db.CVNo == model.CheckVoucherHeaderNo, cancellationToken);
                        await _generalRepo.RemoveRecords<GeneralLedgerBook>(gl => gl.Reference == model.CheckVoucherHeaderNo, cancellationToken);

                        //re-compute amount paid in trade and payment voucher
                        #region -- Recalculate payment of RR's or DR's

                        var getCheckVoucherTradePayment = await _dbContext.CVTradePayments
                            .Where(cv => cv.CheckVoucherId == id)
                            .Include(cv => cv.CV)
                            .ToListAsync(cancellationToken);

                        foreach (var item in getCheckVoucherTradePayment)
                        {
                            if (item.DocumentType == "RR")
                            {
                                var receivingReport = await _dbContext.ReceivingReports.FirstOrDefaultAsync(x => x.ReceivingReportId == item.DocumentId, cancellationToken);

                                receivingReport!.IsPaid = false;
                                receivingReport.AmountPaid -= item.AmountPaid;
                            }
                        }

                        #endregion -- Recalculate payment of RR's or DR's

                        #region --Audit Trail Recording

                        if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy,
                                $"Voided check voucher# {model.CheckVoucherHeaderNo}", "Check Voucher Trade", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Check Voucher has been Voided.";

                        return RedirectToAction(nameof(Index));
                    }
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
        public IActionResult GetAllCheckVoucherIds()
        {
            var cvIds = _dbContext.CheckVoucherHeaders
                                     .Select(cv => cv.CheckVoucherHeaderId) // Assuming Id is the primary key
                                     .ToList();

            return Json(cvIds);
        }
    }
}
