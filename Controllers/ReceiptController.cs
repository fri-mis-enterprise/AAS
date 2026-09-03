using System.Globalization;
using Accounting_System.Data;
using Accounting_System.Models;
using Accounting_System.Models.AccountsReceivable;
using Accounting_System.Models.Reports;
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
    public class ReceiptController : Controller
    {
        private readonly ApplicationDbContext _dbContext;

        private readonly UserManager<IdentityUser> _userManager;

        private readonly ReceiptRepo _receiptRepo;

        private readonly SalesInvoiceRepo _salesInvoiceRepo;

        private readonly ServiceInvoiceRepo _serviceInvoiceRepo;

        private readonly IWebHostEnvironment _webHostEnvironment;

        private readonly GeneralRepo _generalRepo;

        public ReceiptController(ApplicationDbContext dbContext, UserManager<IdentityUser> userManager, ReceiptRepo receiptRepo, IWebHostEnvironment webHostEnvironment, GeneralRepo generalRepo, SalesInvoiceRepo salesInvoiceRepo, ServiceInvoiceRepo serviceInvoiceRepo)
        {
            _dbContext = dbContext;
            _userManager = userManager;
            _receiptRepo = receiptRepo;
            _webHostEnvironment = webHostEnvironment;
            _generalRepo = generalRepo;
            _salesInvoiceRepo = salesInvoiceRepo;
            _serviceInvoiceRepo = serviceInvoiceRepo;
        }

        public IActionResult CollectionIndex()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> GetCollectionReceipts([FromForm] DataTablesParameters parameters, CancellationToken cancellationToken)
        {
            try
            {
                var collectionReceipts = await _receiptRepo.GetCollectionReceiptsAsync(cancellationToken);
                // Search filter
                if (!string.IsNullOrEmpty(parameters.Search.Value))
                {
                    var searchValue = parameters.Search.Value.ToLower();
                    collectionReceipts = collectionReceipts
                        .Where(cr =>
                            cr.CollectionReceiptNo!.ToLower().Contains(searchValue) ||
                            cr.TransactionDate.ToString("MMM dd, yyyy").ToLower().Contains(searchValue) ||
                            cr.SINo?.ToLower().Contains(searchValue) == true ||
                            cr.SVNo?.ToLower().Contains(searchValue) == true ||
                            cr.MultipleSI?.Contains(searchValue) == true ||
                            cr.Customer!.CustomerName.ToLower().Contains(searchValue) ||
                            cr.Total.ToString(CultureInfo.InvariantCulture).ToLower().Contains(searchValue) ||
                            cr.CreatedBy!.ToLower().Contains(searchValue)
                            )
                        .ToList();
                }
                // Sorting
                if (parameters.Order != null && parameters.Order.Count > 0)
                {
                    var orderColumn = parameters.Order[0];
                    var columnName = parameters.Columns[orderColumn.Column].Data;
                    var sortDirection = orderColumn.Dir.ToLower() == "asc" ? "ascending" : "descending";
                    collectionReceipts = collectionReceipts
                        .AsQueryable()
                        .OrderBy($"{columnName} {sortDirection}")
                        .ToList();
                }
                var totalRecords = collectionReceipts.Count();
                var pagedData = collectionReceipts
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
                return RedirectToAction(nameof(CollectionIndex));
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAllCollectionReceiptIds(CancellationToken cancellationToken)
        {
            var collectionReceiptIds = await _dbContext.CollectionReceipts
                                     .Select(cr => cr.CollectionReceiptId) // Assuming Id is the primary key
                                     .ToListAsync(cancellationToken);
            return Json(collectionReceiptIds);
        }

        [HttpGet]
        public async Task<IActionResult> SingleCollectionCreateForSales(CancellationToken cancellationToken)
        {
            var viewModel = new CollectionReceipt
            {
                Customers = await _dbContext.Customers
                    .OrderBy(c => c.CustomerId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.CustomerId.ToString(),
                        Text = s.CustomerName
                    })
                    .ToListAsync(cancellationToken),
                ChartOfAccounts = await _dbContext.ChartOfAccounts
                    .Where(coa => !coa.HasChildren)
                    .OrderBy(coa => coa.AccountId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.AccountNumber,
                        Text = s.AccountNumber + " " + s.AccountName
                    })
                    .ToListAsync(cancellationToken)
            };

            return View(viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> SingleCollectionCreateForSales(CollectionReceipt model, string[] accountTitleText, decimal[] accountAmount, string[] accountTitle, IFormFile? bir2306, IFormFile? bir2307, CancellationToken cancellationToken)
        {
            model.Customers = await _dbContext.Customers
               .OrderBy(c => c.CustomerId)
               .Select(s => new SelectListItem
               {
                   Value = s.Number.ToString(),
                   Text = s.CustomerName
               })
               .ToListAsync(cancellationToken);

            model.SalesInvoices = await _dbContext.SalesInvoices
                .Where(si => !si.IsPaid && si.CustomerId == model.CustomerId && si.IsPosted)
                .OrderBy(si => si.SalesInvoiceId)
                .Select(s => new SelectListItem
                {
                    Value = s.SalesInvoiceId.ToString(),
                    Text = s.SalesInvoiceNo
                })
                .ToListAsync(cancellationToken);

            model.ChartOfAccounts = await _dbContext.ChartOfAccounts
                .Where(coa => !coa.HasChildren)
                .OrderBy(coa => coa.AccountId)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);

            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Validating the series

                    var generateCrNo = await _receiptRepo.GenerateCRNo(cancellationToken);
                    var getLastNumber = long.Parse(generateCrNo.Substring(2));

                    if (getLastNumber > 9999999999)
                    {
                        TempData["error"] = "You reach the maximum Series Number";
                        return View(model);
                    }
                    var totalRemainingSeries = 9999999999 - getLastNumber;
                    if (getLastNumber >= 9999999899)
                    {
                        TempData["warning"] = $"Collection Receipt created successfully, Warning {totalRemainingSeries} series number remaining";
                    }
                    else
                    {
                        TempData["success"] = "Collection Receipt created successfully";
                    }

                    #endregion --Validating the series

                    #region --Saving default value

                    var computeTotalInModelIfZero = model.CashAmount + model.CheckAmount + model.ManagerCheckAmount + model.EWT + model.WVAT;
                    if (computeTotalInModelIfZero == 0)
                    {
                        TempData["error"] = "Please input atleast one type form of payment";
                        return View(model);
                    }
                    var existingSalesInvoice = await _dbContext.SalesInvoices
                                                   .FirstOrDefaultAsync(si => si.SalesInvoiceId == model.SalesInvoiceId, cancellationToken);

                    model.SINo = existingSalesInvoice!.SalesInvoiceNo;
                    model.CollectionReceiptNo = generateCrNo;
                    model.CreatedBy = createdBy;
                    model.Total = computeTotalInModelIfZero;

                        if (bir2306 != null && bir2306.Length > 0)
                        {
                            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2306");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            string fileName = Path.GetFileName(bir2306.FileName);
                            string fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2306.CopyToAsync(stream, cancellationToken);
                            }

                            model.F2306FilePath = fileSavePath;
                            model.IsCertificateUpload = true;
                        }

                        if (bir2307 != null && bir2307.Length > 0)
                        {
                            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2307");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            string fileName = Path.GetFileName(bir2307.FileName);
                            string fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2307.CopyToAsync(stream, cancellationToken);
                            }

                            model.F2307FilePath = fileSavePath;
                            model.IsCertificateUpload = true;
                        }

                    await _dbContext.AddAsync(model, cancellationToken);

                    #endregion --Saving default value

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Create new collection receipt# {model.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    #region --Offsetting function

                    var offsettings = new List<Offsetting>();

                    for (int i = 0; i < accountTitle.Length; i++)
                    {
                        var currentAccountTitle = accountTitleText[i];
                        var currentAccountAmount = accountAmount[i];

                        var splitAccountTitle = currentAccountTitle.Split([' '], 2);

                        offsettings.Add(
                            new Offsetting
                            {
                                AccountNo = accountTitle[i],
                                AccountTitle = splitAccountTitle.Length > 1 ? splitAccountTitle[1] : splitAccountTitle[0],
                                Source = model.CollectionReceiptNo,
                                Reference = model.SINo,
                                Amount = currentAccountAmount,
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            }
                        );
                    }

                    await _dbContext.AddRangeAsync(offsettings, cancellationToken);

                    #endregion --Offsetting function

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return RedirectToAction(nameof(CollectionIndex));
                }
                catch (Exception ex)
                {
                 await transaction.RollbackAsync(cancellationToken);
                 TempData["error"] = ex.Message;
                 return RedirectToAction(nameof(CollectionIndex));
                }
            }
            else
            {
                TempData["error"] = "The information you submitted is not valid!";
                return View(model);
            }
        }

        [HttpGet]
        public async Task<IActionResult> MultipleCollectionCreateForSales(CancellationToken cancellationToken)
        {
            var viewModel = new CollectionReceipt
            {
                Customers = await _dbContext.Customers
                    .OrderBy(c => c.CustomerId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.CustomerId.ToString(),
                        Text = s.CustomerName
                    })
                    .ToListAsync(cancellationToken),
                ChartOfAccounts = await _dbContext.ChartOfAccounts
                    .Where(coa => !coa.HasChildren)
                    .OrderBy(coa => coa.AccountId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.AccountNumber,
                        Text = s.AccountNumber + " " + s.AccountName
                    })
                    .ToListAsync(cancellationToken)
            };

            return View(viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> MultipleCollectionCreateForSales(CollectionReceipt model, string[] accountTitleText, decimal[] accountAmount, string[] accountTitle, IFormFile? bir2306, IFormFile? bir2307, CancellationToken cancellationToken)
        {
            model.Customers = await _dbContext.Customers
               .OrderBy(c => c.CustomerId)
               .Select(s => new SelectListItem
               {
                   Value = s.Number.ToString(),
                   Text = s.CustomerName
               })
               .ToListAsync(cancellationToken);

            model.SalesInvoices = await _dbContext.SalesInvoices
                .Where(si => !si.IsPaid && si.CustomerId == model.CustomerId && si.IsPosted)
                .OrderBy(si => si.SalesInvoiceId)
                .Select(s => new SelectListItem
                {
                    Value = s.SalesInvoiceId.ToString(),
                    Text = s.SalesInvoiceNo
                })
                .ToListAsync(cancellationToken);

            model.ChartOfAccounts = await _dbContext.ChartOfAccounts
                .Where(coa => !coa.HasChildren)
                .OrderBy(coa => coa.AccountId)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);

            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Validating the series

                    var generateCrNo = await _receiptRepo.GenerateCRNo(cancellationToken);
                    var getLastNumber = long.Parse(generateCrNo.Substring(2));

                    if (getLastNumber > 9999999999)
                    {
                        TempData["error"] = "You reach the maximum Series Number";
                        return View(model);
                    }
                    var totalRemainingSeries = 9999999999 - getLastNumber;
                    if (getLastNumber >= 9999999899)
                    {
                        TempData["warning"] = $"Collection Receipt created successfully, Warning {totalRemainingSeries} series number remaining";
                    }
                    else
                    {
                        TempData["success"] = "Collection Receipt created successfully";
                    }

                    #endregion --Validating the series

                    #region --Saving default value

                    var computeTotalInModelIfZero = model.CashAmount + model.CheckAmount + model.ManagerCheckAmount + model.EWT + model.WVAT;
                    if (computeTotalInModelIfZero == 0)
                    {
                        TempData["error"] = "Please input atleast one type form of payment";
                        return View(model);
                    }

                    model.MultipleSI = new string[model.MultipleSIId!.Length];
                    model.MultipleTransactionDate = new DateOnly[model.MultipleSIId.Length];
                    for (int i = 0; i < model.MultipleSIId.Length; i++)
                    {
                        var siId = model.MultipleSIId[i];
                        var salesInvoice = await _dbContext.SalesInvoices
                            .FirstOrDefaultAsync(si => si.SalesInvoiceId == siId, cancellationToken);

                        if (salesInvoice != null)
                        {
                            model.MultipleSI[i] = salesInvoice.SalesInvoiceNo!;
                            model.MultipleTransactionDate[i] = salesInvoice.TransactionDate;
                        }
                    }

                    model.CollectionReceiptNo = generateCrNo;
                    model.CreatedBy = createdBy;
                    model.Total = computeTotalInModelIfZero;

                        if (bir2306 != null && bir2306.Length > 0)
                        {
                            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2306");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            string fileName = Path.GetFileName(bir2306.FileName);
                            string fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2306.CopyToAsync(stream, cancellationToken);
                            }

                            model.F2306FilePath = fileSavePath;
                            model.IsCertificateUpload = true;
                        }

                        if (bir2307 != null && bir2307.Length > 0)
                        {
                            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2307");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            var fileName = Path.GetFileName(bir2307.FileName);
                            var fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2307.CopyToAsync(stream, cancellationToken);
                            }

                            model.F2307FilePath = fileSavePath;
                            model.IsCertificateUpload = true;
                        }

                    await _dbContext.AddAsync(model, cancellationToken);

                    #endregion --Saving default value

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Create new collection receipt# {model.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    #region --Offsetting function

                    var offsettings = new List<Offsetting>();

                    for (int i = 0; i < accountTitle.Length; i++)
                    {
                        var currentAccountTitle = accountTitleText[i];
                        var currentAccountAmount = accountAmount[i];

                        var splitAccountTitle = currentAccountTitle.Split([' '], 2);

                        offsettings.Add(
                            new Offsetting
                            {
                                AccountNo = accountTitle[i],
                                AccountTitle = splitAccountTitle.Length > 1 ? splitAccountTitle[1] : splitAccountTitle[0],
                                Source = model.CollectionReceiptNo,
                                Reference = model.SINo,
                                Amount = currentAccountAmount,
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            }
                        );
                    }

                    await _dbContext.AddRangeAsync(offsettings, cancellationToken);

                    #endregion --Offsetting function

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return RedirectToAction(nameof(CollectionIndex));
                }
                catch (Exception ex)
                {
                 await transaction.RollbackAsync(cancellationToken);
                 TempData["error"] = ex.Message;
                 return RedirectToAction(nameof(CollectionIndex));
                }
            }
            else
            {
                TempData["error"] = "The information you submitted is not valid!";
                return View(model);
            }
        }

        [HttpGet]
        public async Task<IActionResult> CollectionCreateForService(CancellationToken cancellationToken)
        {
            var viewModel = new CollectionReceipt
            {
                Customers = await _dbContext.Customers
                    .OrderBy(c => c.CustomerId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.CustomerId.ToString(),
                        Text = s.CustomerName
                    })
                    .ToListAsync(cancellationToken),
                ChartOfAccounts = await _dbContext.ChartOfAccounts
                    .Where(coa => !coa.HasChildren)
                    .OrderBy(coa => coa.AccountId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.AccountNumber,
                        Text = s.AccountNumber + " " + s.AccountName
                    })
                    .ToListAsync(cancellationToken)
            };

            return View(viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> CollectionCreateForService(CollectionReceipt model, string[] accountTitleText, decimal[] accountAmount, string[] accountTitle, IFormFile? bir2306, IFormFile? bir2307, CancellationToken cancellationToken)
        {
            model.Customers = await _dbContext.Customers
               .OrderBy(c => c.CustomerId)
               .Select(s => new SelectListItem
               {
                   Value = s.CustomerId.ToString(),
                   Text = s.CustomerName
               })
               .ToListAsync(cancellationToken);

            model.SalesInvoices = await _dbContext.ServiceInvoices
                .Where(si => !si.IsPaid && si.CustomerId == model.CustomerId && si.IsPosted)
                .OrderBy(si => si.ServiceInvoiceId)
                .Select(s => new SelectListItem
                {
                    Value = s.ServiceInvoiceId.ToString(),
                    Text = s.ServiceInvoiceNo
                })
                .ToListAsync(cancellationToken);

            model.ChartOfAccounts = await _dbContext.ChartOfAccounts
                .Where(coa => !coa.HasChildren)
                .OrderBy(coa => coa.AccountId)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);

            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Validating the series

                    var generateCrNo = await _receiptRepo.GenerateCRNo(cancellationToken);
                    var getLastNumber = long.Parse(generateCrNo.Substring(2));

                    if (getLastNumber > 9999999999)
                    {
                        TempData["error"] = "You reach the maximum Series Number";
                        return View(model);
                    }
                    var totalRemainingSeries = 9999999999 - getLastNumber;
                    if (getLastNumber >= 9999999899)
                    {
                        TempData["warning"] = $"Collection Receipt created successfully, Warning {totalRemainingSeries} series number remaining";
                    }
                    else
                    {
                        TempData["success"] = "Collection Receipt created successfully";
                    }

                    #endregion --Validating the series

                    #region --Saving default value

                    var computeTotalInModelIfZero = model.CashAmount + model.CheckAmount + model.ManagerCheckAmount + model.EWT + model.WVAT;
                    if (computeTotalInModelIfZero == 0)
                    {
                        TempData["error"] = "Please input atleast one type form of payment";
                        return View(model);
                    }
                    var existingServiceInvoice = await _dbContext.ServiceInvoices
                                                   .FirstOrDefaultAsync(si => si.ServiceInvoiceId == model.ServiceInvoiceId, cancellationToken);

                    model.SVNo = existingServiceInvoice!.ServiceInvoiceNo;
                    model.CollectionReceiptNo = generateCrNo;
                    model.CreatedBy = createdBy;
                    model.Total = computeTotalInModelIfZero;

                        if (bir2306 != null && bir2306.Length > 0)
                        {
                            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2306");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            string fileName = Path.GetFileName(bir2306.FileName);
                            string fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2306.CopyToAsync(stream, cancellationToken);
                            }

                            model.F2306FilePath = fileSavePath;
                            model.IsCertificateUpload = true;
                        }

                        if (bir2307 != null && bir2307.Length > 0)
                        {
                            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2307");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            string fileName = Path.GetFileName(bir2307.FileName);
                            string fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2307.CopyToAsync(stream, cancellationToken);
                            }

                            model.F2307FilePath = fileSavePath;
                            model.IsCertificateUpload = true;
                        }

                    await _dbContext.AddAsync(model, cancellationToken);

                    #endregion --Saving default value

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Create new collection receipt# {model.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    #region --Offsetting function

                    var offsettings = new List<Offsetting>();

                    for (int i = 0; i < accountTitle.Length; i++)
                    {
                        var currentAccountTitle = accountTitleText[i];
                        var currentAccountAmount = accountAmount[i];

                        var splitAccountTitle = currentAccountTitle.Split([' '], 2);

                        offsettings.Add(
                            new Offsetting
                            {
                                AccountNo = accountTitle[i],
                                AccountTitle = splitAccountTitle.Length > 1 ? splitAccountTitle[1] : splitAccountTitle[0],
                                Source = model.CollectionReceiptNo,
                                Reference = model.SVNo,
                                Amount = currentAccountAmount,
                                CreatedBy = model.CreatedBy,
                                CreatedDate = model.CreatedDate
                            }
                        );
                    }

                    await _dbContext.AddRangeAsync(offsettings, cancellationToken);

                    #endregion --Offsetting function

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return RedirectToAction(nameof(CollectionIndex));
                }
                catch (Exception ex)
                {
                 await transaction.RollbackAsync(cancellationToken);
                 TempData["error"] = ex.Message;
                 return RedirectToAction(nameof(CollectionIndex));
                }
            }
            else
            {
                TempData["error"] = "The information you submitted is not valid!";
                return View(model);
            }
        }

        public async Task<IActionResult> CollectionPrint(int id, CancellationToken cancellationToken)
        {
            var cr = await _receiptRepo.FindCR(id, cancellationToken);
            return View(cr);
        }
        public async Task<IActionResult> MultipleCollectionPrint(int id, CancellationToken cancellationToken)
        {
            var cr = await _receiptRepo.FindCR(id, cancellationToken);
            return View(cr);
        }

        public async Task<IActionResult> PrintedCollectionReceipt(int id, CancellationToken cancellationToken)
        {
            var findIdOfCr = await _receiptRepo.FindCR(id, cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            if (!findIdOfCr.IsPrinted)
            {

                #region --Audit Trail Recording

                if (findIdOfCr.OriginalSeriesNumber.IsNullOrEmpty() && findIdOfCr.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy, $"Printed original copy of cr# {findIdOfCr.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                findIdOfCr.IsPrinted = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return RedirectToAction(nameof(CollectionPrint), new { id });
        }
        public async Task<IActionResult> PrintedMultipleCR(int id, CancellationToken cancellationToken)
        {
            var findIdOfCr = await _receiptRepo.FindCR(id, cancellationToken);
            var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            if (!findIdOfCr.IsPrinted)
            {

                #region --Audit Trail Recording

                if (findIdOfCr.OriginalSeriesNumber.IsNullOrEmpty() && findIdOfCr.OriginalDocumentId == 0)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    AuditTrail auditTrailBook = new(createdBy, $"Printed original copy of cr# {findIdOfCr.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                    await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                }

                #endregion --Audit Trail Recording

                findIdOfCr.IsPrinted = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return RedirectToAction(nameof(MultipleCollectionPrint), new { id });
        }

        [HttpGet]
        public async Task<IActionResult> GetSalesInvoices(int customerNo, CancellationToken cancellationToken)
        {
            var invoices = await _dbContext
                .SalesInvoices
                .Where(si => si.CustomerId == customerNo && !si.IsPaid && si.IsPosted)
                .OrderBy(si => si.SalesInvoiceId)
                .ToListAsync(cancellationToken);

            var invoiceList = invoices.Select(si => new SelectListItem
            {
                Value = si.SalesInvoiceId.ToString(),   // Replace with your actual ID property
                Text = si.SalesInvoiceNo              // Replace with your actual property for display text
            }).ToList();

            return Json(invoiceList);
        }

        [HttpGet]
        public async Task<IActionResult> GetServiceInvoices(int customerNo, CancellationToken cancellationToken)
        {
            var invoices = await _dbContext
                .ServiceInvoices
                .Where(si => si.CustomerId == customerNo && !si.IsPaid && si.IsPosted)
                .OrderBy(si => si.ServiceInvoiceId)
                .ToListAsync(cancellationToken);

            var invoiceList = invoices.Select(si => new SelectListItem
            {
                Value = si.ServiceInvoiceId.ToString(),   // Replace with your actual ID property
                Text = si.ServiceInvoiceNo              // Replace with your actual property for display text
            }).ToList();

            return Json(invoiceList);
        }

        [HttpGet]
        public async Task<IActionResult> GetInvoiceDetails(int invoiceNo, bool isSales, bool isServices, CancellationToken cancellationToken)
        {
            if (isSales && !isServices)
            {
                var si = await _dbContext
                .SalesInvoices
                .Include(c => c.Customer)
                .FirstOrDefaultAsync(si => si.SalesInvoiceId == invoiceNo, cancellationToken);

                var netDiscount = si!.Amount - si.Discount;
                var netOfVatAmount = si.Customer!.CustomerType == CS.VatType_Vatable ? _generalRepo.ComputeNetOfVat(netDiscount) : netDiscount;
                var withHoldingTaxAmount = si.Customer.WithHoldingTax ? _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.01m) : 0;
                var withHoldingVatAmount = si.Customer.WithHoldingVat ? _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.05m) : 0;

                return Json(new
                {
                    Amount = netDiscount.ToString("N2"),
                    AmountPaid = si.AmountPaid.ToString("N2"),
                    Balance = si.Balance.ToString("N2"),
                    Ewt = withHoldingTaxAmount.ToString("N2"),
                    Wvat = withHoldingVatAmount.ToString("N2"),
                    Total = (netDiscount - (withHoldingTaxAmount + withHoldingVatAmount)).ToString("N2")
                });
            }
            else if (isServices && !isSales)
            {
                var sv = await _dbContext
                .ServiceInvoices
                .Include(c => c.Customer)
                .FirstOrDefaultAsync(si => si.ServiceInvoiceId == invoiceNo, cancellationToken);

                var netOfVatAmount = sv!.Customer!.CustomerType == CS.VatType_Vatable ? _generalRepo.ComputeNetOfVat(sv.Amount) - sv.Discount : sv.Amount - sv.Discount;
                var withHoldingTaxAmount = sv.Customer.WithHoldingTax ? _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.01m) : 0;
                var withHoldingVatAmount = sv.Customer.WithHoldingVat ? _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.05m) : 0;

                return Json(new
                {
                    Amount = sv.Total.ToString("N2"),
                    AmountPaid = sv.AmountPaid.ToString("N2"),
                    Balance = sv.Balance.ToString("N2"),
                    Ewt = withHoldingTaxAmount.ToString("N2"),
                    Wvat = withHoldingVatAmount.ToString("N2"),
                    Total = (sv.Total - (withHoldingTaxAmount + withHoldingVatAmount)).ToString("N2")
                });
            }
            return Json(null);
        }

        public async Task<IActionResult> MultipleInvoiceBalance(int siNo, CancellationToken cancellationToken)
        {
            var salesInvoice = await _dbContext.SalesInvoices
                .Include(c => c.Customer)
                .FirstOrDefaultAsync(si => si.SalesInvoiceId == siNo, cancellationToken);
            if (salesInvoice != null)
            {
                var amount = salesInvoice.Amount;
                var amountPaid = salesInvoice.AmountPaid;
                var netAmount = salesInvoice.Amount - salesInvoice.Discount;
                var vatAmount = salesInvoice.Customer!.CustomerType == CS.VatType_Vatable ? _generalRepo.ComputeVatAmount((netAmount / 1.12m) * 0.12m) : 0;
                var ewtAmount = salesInvoice.Customer.WithHoldingTax ? _generalRepo.ComputeEwtAmount((netAmount / 1.12m), 0.01m) : 0;
                var wvatAmount = salesInvoice.Customer.WithHoldingVat ? _generalRepo.ComputeEwtAmount((netAmount / 1.12m), 0.05m) : 0;
                var balance = amount - amountPaid;

                return Json(new
                {
                    Amount = amount,
                    AmountPaid = amountPaid,
                    NetAmount = netAmount,
                    VatAmount = vatAmount,
                    EwtAmount = ewtAmount,
                    WvatAmount = wvatAmount,
                    Balance = balance
                });
            }
            return Json(null);
        }

        [HttpGet]
        public async Task<IActionResult> GetMultipleInvoiceDetails(int[] siNo, bool isSales, CancellationToken cancellationToken)
        {
            if (isSales)
            {
                var si = await _dbContext
                .SalesInvoices
                .Include(x => x.Customer)
                .FirstOrDefaultAsync(si => siNo.Contains(si.SalesInvoiceId), cancellationToken);

                var netDiscount = si!.Amount - si.Discount;
                var netOfVatAmount = si.Customer!.CustomerType == CS.VatType_Vatable ? _generalRepo.ComputeNetOfVat(netDiscount) : netDiscount;
                var withHoldingTaxAmount = si.Customer.WithHoldingTax ? _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.01m) : 0;
                var withHoldingVatAmount = si.Customer.WithHoldingVat ? _generalRepo.ComputeEwtAmount(netOfVatAmount, 0.05m) : 0;

                return Json(new
                {
                    Amount = netDiscount,
                    si.AmountPaid,
                    si.Balance,
                    WithholdingTax = withHoldingTaxAmount,
                    WithholdingVat = withHoldingVatAmount,
                    Total = netDiscount - (withHoldingTaxAmount + withHoldingVatAmount)
                });
            }
            return Json(null);
        }

        [HttpGet]
        public async Task<IActionResult> CollectionEdit(int? id, CancellationToken cancellationToken)
        {
            if (id == null)
            {
                return NotFound();
            }
            var existingModel = await _dbContext.CollectionReceipts.FirstOrDefaultAsync(x => x.CollectionReceiptId == id, cancellationToken);

            if (existingModel == null)
            {
                return NotFound();
            }

            existingModel.Customers = await _dbContext.Customers
               .OrderBy(c => c.CustomerId)
               .Select(s => new SelectListItem
               {
                   Value = s.CustomerId.ToString(),
                   Text = s.CustomerName
               })
               .ToListAsync(cancellationToken);

            existingModel.SalesInvoices = await _dbContext.SalesInvoices
                .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId && si.IsPosted)
                .OrderBy(si => si.SalesInvoiceId)
                .Select(s => new SelectListItem
                {
                    Value = s.SalesInvoiceId.ToString(),
                    Text = s.SalesInvoiceNo
                })
                .ToListAsync(cancellationToken);

            existingModel.ServiceInvoices = await _dbContext.ServiceInvoices
                .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId && si.IsPosted)
                .OrderBy(si => si.ServiceInvoiceId)
                .Select(s => new SelectListItem
                {
                    Value = s.ServiceInvoiceId.ToString(),
                    Text = s.ServiceInvoiceNo
                })
                .ToListAsync(cancellationToken);

            existingModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                .Where(coa => !coa.HasChildren)
                .OrderBy(coa => coa.AccountId)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);

            var findCustomers = await _dbContext.Customers
                .FirstOrDefaultAsync(c => c.CustomerId == existingModel.CustomerId, cancellationToken);

            var offsettings = await _dbContext.Offsettings
                .Where(offset => offset.Source == existingModel.CollectionReceiptNo)
                .ToListAsync(cancellationToken);

            ViewBag.CustomerName = findCustomers?.CustomerName;
            ViewBag.Offsettings = offsettings;

            return View(existingModel);
        }

        [HttpPost]
        public async Task<IActionResult> CollectionEdit(CollectionReceipt model, string[] accountTitleText, decimal[] accountAmount, string[] accountTitle, IFormFile? bir2306, IFormFile? bir2307, CancellationToken cancellationToken)
        {
            var existingModel = await _receiptRepo.FindCR(model.CollectionReceiptId, cancellationToken);

            var offsettings = await _dbContext.Offsettings
                .Where(offset => offset.Source == existingModel.CollectionReceiptNo)
                .ToListAsync(cancellationToken);

            ViewBag.Offsettings = offsettings;
            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Saving default value

                    var computeTotalInModelIfZero = model.CashAmount + model.CheckAmount + model.ManagerCheckAmount + model.EWT + model.WVAT;
                    if (computeTotalInModelIfZero == 0)
                    {
                        TempData["error"] = "Please input atleast one type form of payment";
                        existingModel.Customers = await _dbContext.Customers
                            .OrderBy(c => c.CustomerId)
                            .Select(s => new SelectListItem
                            {
                                Value = s.CustomerId.ToString(),
                                Text = s.CustomerName
                            })
                            .ToListAsync(cancellationToken);

                        existingModel.SalesInvoices = await _dbContext.SalesInvoices
                            .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId && si.IsPosted)
                            .OrderBy(si => si.SalesInvoiceId)
                            .Select(s => new SelectListItem
                            {
                                Value = s.SalesInvoiceId.ToString(),
                                Text = s.SalesInvoiceNo
                            })
                            .ToListAsync(cancellationToken);

                        existingModel.ServiceInvoices = await _dbContext.ServiceInvoices
                            .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId && si.IsPosted)
                            .OrderBy(si => si.ServiceInvoiceId)
                            .Select(s => new SelectListItem
                            {
                                Value = s.ServiceInvoiceId.ToString(),
                                Text = s.ServiceInvoiceNo
                            })
                            .ToListAsync(cancellationToken);

                        existingModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                            .Where(coa => !coa.HasChildren)
                            .OrderBy(coa => coa.AccountId)
                            .Select(s => new SelectListItem
                            {
                                Value = s.AccountNumber,
                                Text = s.AccountNumber + " " + s.AccountName
                            })
                            .ToListAsync(cancellationToken);
                        return View(existingModel);
                    }

                    existingModel.TransactionDate = model.TransactionDate;
                    existingModel.ReferenceNo = model.ReferenceNo;
                    existingModel.Remarks = model.Remarks;
                    existingModel.CheckDate = model.CheckDate;
                    existingModel.CheckNo = model.CheckNo;
                    existingModel.CheckBank = model.CheckBank;
                    existingModel.CheckBranch = model.CheckBranch;
                    existingModel.CashAmount = model.CashAmount;
                    existingModel.CheckAmount = model.CheckAmount;
                    existingModel.ManagerCheckAmount = model.ManagerCheckAmount;
                    existingModel.EWT = model.EWT;
                    existingModel.WVAT = model.WVAT;
                    existingModel.Total = computeTotalInModelIfZero;

                        if (bir2306 != null && bir2306.Length > 0)
                        {
                            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2306");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            var fileName = Path.GetFileName(bir2306.FileName);
                            var fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2306.CopyToAsync(stream, cancellationToken);
                            }

                            existingModel.F2306FilePath = fileSavePath;
                            existingModel.IsCertificateUpload = true;
                        }

                        if (bir2307 != null && bir2307.Length > 0)
                        {
                            var uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2307");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            var fileName = Path.GetFileName(bir2307.FileName);
                            var fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2307.CopyToAsync(stream, cancellationToken);
                            }

                            existingModel.F2307FilePath = fileSavePath;
                            existingModel.IsCertificateUpload = true;
                        }

                        #endregion --Saving default value

                    #region --Offsetting function

                    var findOffsettings = await _dbContext.Offsettings
                    .Where(offset => offset.Source == existingModel.CollectionReceiptNo)
                    .ToListAsync(cancellationToken);

                    var accountTitleSet = new HashSet<string>(accountTitle);

                    // Remove records not in accountTitle
                    foreach (var offsetting in findOffsettings)
                    {
                        if (!accountTitleSet.Contains(offsetting.AccountNo))
                        {
                            _dbContext.Offsettings.Remove(offsetting);
                        }
                    }

                    // Dictionary to keep track of AccountNo and their ids for comparison
                    var accountTitleDict = new Dictionary<string, List<int>>();
                    foreach (var offsetting in findOffsettings)
                    {
                        if (!accountTitleDict.ContainsKey(offsetting.AccountNo))
                        {
                            accountTitleDict[offsetting.AccountNo] = new List<int>();
                        }
                        accountTitleDict[offsetting.AccountNo].Add(offsetting.Id);
                    }

                    // Add or update records
                    for (int i = 0; i < accountTitle.Length; i++)
                    {
                        var accountNo = accountTitle[i];
                        var currentAccountTitle = accountTitleText[i];
                        var currentAccountAmount = accountAmount[i];

                        var splitAccountTitle = currentAccountTitle.Split([' '], 2);

                        if (accountTitleDict.TryGetValue(accountNo, out var ids))
                        {
                            // Update the first matching record and remove it from the list
                            var offsettingId = ids.First();
                            ids.RemoveAt(0);
                            var offsetting = findOffsettings.First(o => o.Id == offsettingId);

                            offsetting.AccountTitle = splitAccountTitle.Length > 1 ? splitAccountTitle[1] : splitAccountTitle[0];
                            offsetting.Amount = currentAccountAmount;

                            if (ids.Count == 0)
                            {
                                accountTitleDict.Remove(accountNo);
                            }
                        }
                        else
                        {
                            // Add new record
                            var newOffsetting = new Offsetting
                            {
                                AccountNo = accountNo,
                                AccountTitle = splitAccountTitle.Length > 1 ? splitAccountTitle[1] : splitAccountTitle[0],
                                Source = existingModel.CollectionReceiptNo!,
                                Reference = existingModel.SINo ?? existingModel.SVNo,
                                Amount = currentAccountAmount,
                            };
                            await _dbContext.Offsettings.AddAsync(newOffsetting, cancellationToken);
                        }
                    }

                    // Remove remaining records that were duplicates
                    foreach (var ids in accountTitleDict.Values)
                    {
                        foreach (var id in ids)
                        {
                            var offsetting = findOffsettings.First(o => o.Id == id);
                            _dbContext.Offsettings.Remove(offsetting);
                        }
                    }

                    #endregion --Offsetting function

                    if (_dbContext.ChangeTracker.HasChanges())
                    {
                        #region --Audit Trail Recording

                        if (existingModel.OriginalSeriesNumber.IsNullOrEmpty() && existingModel.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Edited collection receipt# {existingModel.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Collection Receipt edited successfully";
                        return RedirectToAction(nameof(CollectionIndex));
                    }
                    else
                    {
                        throw new InvalidOperationException("No data changes!");
                    }
                }
                catch (Exception ex)
                {
                 await transaction.RollbackAsync(cancellationToken);
                 existingModel.Customers = await _dbContext.Customers
                     .OrderBy(c => c.CustomerId)
                     .Select(s => new SelectListItem
                     {
                         Value = s.CustomerId.ToString(),
                         Text = s.CustomerName
                     })
                     .ToListAsync(cancellationToken);

                 existingModel.SalesInvoices = await _dbContext.SalesInvoices
                     .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId && si.IsPosted)
                     .OrderBy(si => si.SalesInvoiceId)
                     .Select(s => new SelectListItem
                     {
                         Value = s.SalesInvoiceId.ToString(),
                         Text = s.SalesInvoiceNo
                     })
                     .ToListAsync(cancellationToken);

                 existingModel.ServiceInvoices = await _dbContext.ServiceInvoices
                     .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId && si.IsPosted)
                     .OrderBy(si => si.ServiceInvoiceId)
                     .Select(s => new SelectListItem
                     {
                         Value = s.ServiceInvoiceId.ToString(),
                         Text = s.ServiceInvoiceNo
                     })
                     .ToListAsync(cancellationToken);

                 existingModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                     .Where(coa => !coa.HasChildren)
                     .OrderBy(coa => coa.AccountId)
                     .Select(s => new SelectListItem
                     {
                         Value = s.AccountNumber,
                         Text = s.AccountNumber + " " + s.AccountName
                     })
                     .ToListAsync(cancellationToken);
                 TempData["error"] = ex.Message;
                 return View(existingModel);
                }
            }
            else
            {
                TempData["error"] = "The information you submitted is not valid!";
                existingModel.Customers = await _dbContext.Customers
                    .OrderBy(c => c.CustomerId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.CustomerId.ToString(),
                        Text = s.CustomerName
                    })
                    .ToListAsync(cancellationToken);

                existingModel.SalesInvoices = await _dbContext.SalesInvoices
                    .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId && si.IsPosted)
                    .OrderBy(si => si.SalesInvoiceId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.SalesInvoiceId.ToString(),
                        Text = s.SalesInvoiceNo
                    })
                    .ToListAsync(cancellationToken);

                existingModel.ServiceInvoices = await _dbContext.ServiceInvoices
                    .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId && si.IsPosted)
                    .OrderBy(si => si.ServiceInvoiceId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.ServiceInvoiceId.ToString(),
                        Text = s.ServiceInvoiceNo
                    })
                    .ToListAsync(cancellationToken);

                existingModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                    .Where(coa => !coa.HasChildren)
                    .OrderBy(coa => coa.AccountId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.AccountNumber,
                        Text = s.AccountNumber + " " + s.AccountName
                    })
                    .ToListAsync(cancellationToken);
                return View(existingModel);
            }
        }

        [HttpGet]
        public async Task<IActionResult> MultipleCollectionEdit(int? id, CancellationToken cancellationToken)
        {
            if (id == null)
            {
                return NotFound();
            }
            var existingModel = await _dbContext.CollectionReceipts.FirstOrDefaultAsync(x => x.CollectionReceiptId == id, cancellationToken);

            if (existingModel == null)
            {
                return NotFound();
            }

            existingModel.Customers = await _dbContext.Customers
               .OrderBy(c => c.CustomerId)
               .Select(s => new SelectListItem
               {
                   Value = s.CustomerId.ToString(),
                   Text = s.CustomerName
               })
               .ToListAsync(cancellationToken);

            if (existingModel.MultipleSIId != null)
            {
                existingModel.SalesInvoices = await _dbContext.SalesInvoices
                    .Where(si => !si.IsPaid && existingModel.MultipleSIId.Contains(si.SalesInvoiceId))
                    .OrderBy(si => si.SalesInvoiceId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.SalesInvoiceId.ToString(),
                        Text = s.SalesInvoiceNo
                    })
                    .ToListAsync(cancellationToken);
            }

            existingModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                .Where(coa => !coa.HasChildren)
                .OrderBy(coa => coa.AccountId)
                .Select(s => new SelectListItem
                {
                    Value = s.AccountNumber,
                    Text = s.AccountNumber + " " + s.AccountName
                })
                .ToListAsync(cancellationToken);

            var findCustomers = await _dbContext.Customers
                .FirstOrDefaultAsync(c => c.CustomerId == existingModel.CustomerId, cancellationToken);

            var offsettings = await _dbContext.Offsettings
                .Where(offset => offset.Source == existingModel.CollectionReceiptNo)
                .ToListAsync(cancellationToken);

            ViewBag.CustomerName = findCustomers?.CustomerName;
            ViewBag.Offsettings = offsettings;

            return View(existingModel);
        }

        [HttpPost]
        public async Task<IActionResult> MultipleCollectionEdit(CollectionReceipt model, string[] accountTitleText, decimal[] accountAmount, string[] accountTitle, IFormFile? bir2306, IFormFile? bir2307, CancellationToken cancellationToken)
        {
            var existingModel = await _receiptRepo.FindCR(model.CollectionReceiptId, cancellationToken);

            var offsettings = await _dbContext.Offsettings
                .Where(offset => offset.Source == existingModel.CollectionReceiptNo)
                .ToListAsync(cancellationToken);

            ViewBag.Offsettings = offsettings;
            if (ModelState.IsValid)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                try
                {
                    #region --Saving default value

                    var computeTotalInModelIfZero = model.CashAmount + model.CheckAmount + model.ManagerCheckAmount + model.EWT + model.WVAT;
                    if (computeTotalInModelIfZero == 0)
                    {
                        TempData["error"] = "Please input atleast one type form of payment";
                        existingModel.Customers = await _dbContext.Customers
                            .OrderBy(c => c.CustomerId)
                            .Select(s => new SelectListItem
                            {
                                Value = s.CustomerId.ToString(),
                                Text = s.CustomerName
                            })
                            .ToListAsync(cancellationToken);

                        existingModel.SalesInvoices = await _dbContext.SalesInvoices
                            .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId)
                            .OrderBy(si => si.SalesInvoiceId)
                            .Select(s => new SelectListItem
                            {
                                Value = s.SalesInvoiceId.ToString(),
                                Text = s.SalesInvoiceNo
                            })
                            .ToListAsync(cancellationToken);

                        existingModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                            .Where(coa => !coa.HasChildren)
                            .OrderBy(coa => coa.AccountId)
                            .Select(s => new SelectListItem
                            {
                                Value = s.AccountNumber,
                                Text = s.AccountNumber + " " + s.AccountName
                            })
                            .ToListAsync(cancellationToken);
                        return View(existingModel);
                    }

                    existingModel.MultipleSIId = new int[model.MultipleSIId!.Length];
                    existingModel.MultipleSI = new string[model.MultipleSIId.Length];
                    existingModel.SIMultipleAmount = new decimal[model.MultipleSIId.Length];
                    existingModel.MultipleTransactionDate = new DateOnly[model.MultipleSIId.Length];
                    for (int i = 0; i < model.MultipleSIId.Length; i++)
                    {
                        var siId = model.MultipleSIId[i];
                        var salesInvoice = await _dbContext.SalesInvoices
                            .FirstOrDefaultAsync(si => si.SalesInvoiceId == siId, cancellationToken);

                        if (salesInvoice != null)
                        {
                            existingModel.MultipleSIId[i] = model.MultipleSIId[i];
                            existingModel.MultipleSI[i] = salesInvoice.SalesInvoiceNo!;
                            existingModel.MultipleTransactionDate[i] = salesInvoice.TransactionDate;
                            existingModel.SIMultipleAmount[i] = model.SIMultipleAmount![i];
                        }
                    }

                    existingModel.TransactionDate = model.TransactionDate;
                    existingModel.ReferenceNo = model.ReferenceNo;
                    existingModel.Remarks = model.Remarks;
                    existingModel.CheckDate = model.CheckDate;
                    existingModel.CheckNo = model.CheckNo;
                    existingModel.CheckBank = model.CheckBank;
                    existingModel.CheckBranch = model.CheckBranch;
                    existingModel.CashAmount = model.CashAmount;
                    existingModel.CheckAmount = model.CheckAmount;
                    existingModel.ManagerCheckAmount = model.ManagerCheckAmount;
                    existingModel.EWT = model.EWT;
                    existingModel.WVAT = model.WVAT;
                    existingModel.Total = computeTotalInModelIfZero;

                        if (bir2306 != null && bir2306.Length > 0)
                        {
                            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2306");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            string fileName = Path.GetFileName(bir2306.FileName);
                            string fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2306.CopyToAsync(stream, cancellationToken);
                            }

                            existingModel.F2306FilePath = fileSavePath;
                            existingModel.IsCertificateUpload = true;
                        }

                        if (bir2307 != null && bir2307.Length > 0)
                        {
                            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "BIR 2307");

                            if (!Directory.Exists(uploadsFolder))
                            {
                                Directory.CreateDirectory(uploadsFolder);
                            }

                            string fileName = Path.GetFileName(bir2307.FileName);
                            string fileSavePath = Path.Combine(uploadsFolder, fileName);

                            await using (FileStream stream = new FileStream(fileSavePath, FileMode.Create))
                            {
                                await bir2307.CopyToAsync(stream, cancellationToken);
                            }

                            existingModel.F2307FilePath = fileSavePath;
                            existingModel.IsCertificateUpload = true;
                        }

                    #endregion --Saving default value

                    #region --Offsetting function

                    var findOffsettings = await _dbContext.Offsettings
                    .Where(offset => offset.Source == existingModel.CollectionReceiptNo)
                    .ToListAsync(cancellationToken);

                    var accountTitleSet = new HashSet<string>(accountTitle);

                    // Remove records not in accountTitle
                    foreach (var offsetting in findOffsettings)
                    {
                        if (!accountTitleSet.Contains(offsetting.AccountNo))
                        {
                            _dbContext.Offsettings.Remove(offsetting);
                        }
                    }

                    // Dictionary to keep track of AccountNo and their ids for comparison
                    var accountTitleDict = new Dictionary<string, List<int>>();
                    foreach (var offsetting in findOffsettings)
                    {
                        if (!accountTitleDict.ContainsKey(offsetting.AccountNo))
                        {
                            accountTitleDict[offsetting.AccountNo] = new List<int>();
                        }
                        accountTitleDict[offsetting.AccountNo].Add(offsetting.Id);
                    }

                    // Add or update records
                    for (int i = 0; i < accountTitle.Length; i++)
                    {
                        var accountNo = accountTitle[i];
                        var currentAccountTitle = accountTitleText[i];
                        var currentAccountAmount = accountAmount[i];

                        var splitAccountTitle = currentAccountTitle.Split([' '], 2);

                        if (accountTitleDict.TryGetValue(accountNo, out var ids))
                        {
                            // Update the first matching record and remove it from the list
                            var offsettingId = ids.First();
                            ids.RemoveAt(0);
                            var offsetting = findOffsettings.First(o => o.Id == offsettingId);

                            offsetting.AccountTitle = splitAccountTitle.Length > 1 ? splitAccountTitle[1] : splitAccountTitle[0];
                            offsetting.Amount = currentAccountAmount;

                            if (ids.Count == 0)
                            {
                                accountTitleDict.Remove(accountNo);
                            }
                        }
                        else
                        {
                            // Add new record
                            var newOffsetting = new Offsetting
                            {
                                AccountNo = accountNo,
                                AccountTitle = splitAccountTitle.Length > 1 ? splitAccountTitle[1] : splitAccountTitle[0],
                                Source = existingModel.CollectionReceiptNo!,
                                Reference = existingModel.SINo ?? existingModel.SVNo,
                                Amount = currentAccountAmount,
                            };
                            await _dbContext.Offsettings.AddAsync(newOffsetting, cancellationToken);
                        }
                    }

                    // Remove remaining records that were duplicates
                    foreach (var ids in accountTitleDict.Values)
                    {
                        foreach (var id in ids)
                        {
                            var offsetting = findOffsettings.First(o => o.Id == id);
                            _dbContext.Offsettings.Remove(offsetting);
                        }
                    }

                    #endregion --Offsetting function

                    if (_dbContext.ChangeTracker.HasChanges())
                    {
                        #region --Audit Trail Recording

                        if (existingModel.OriginalSeriesNumber.IsNullOrEmpty() && existingModel.OriginalDocumentId == 0)
                        {
                            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                            AuditTrail auditTrailBook = new(createdBy, $"Edited collection receipt# {existingModel.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Collection Receipt edited successfully";
                        return RedirectToAction(nameof(CollectionIndex));
                    }
                    else
                    {
                        throw new InvalidOperationException("No data changes!");
                    }
                }
                catch (Exception ex)
                {
                 await transaction.RollbackAsync(cancellationToken);
                 existingModel.Customers = await _dbContext.Customers
                     .OrderBy(c => c.CustomerId)
                     .Select(s => new SelectListItem
                     {
                         Value = s.CustomerId.ToString(),
                         Text = s.CustomerName
                     })
                     .ToListAsync(cancellationToken);

                 existingModel.SalesInvoices = await _dbContext.SalesInvoices
                     .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId)
                     .OrderBy(si => si.SalesInvoiceId)
                     .Select(s => new SelectListItem
                     {
                         Value = s.SalesInvoiceId.ToString(),
                         Text = s.SalesInvoiceNo
                     })
                     .ToListAsync(cancellationToken);

                 existingModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                     .Where(coa => !coa.HasChildren)
                     .OrderBy(coa => coa.AccountId)
                     .Select(s => new SelectListItem
                     {
                         Value = s.AccountNumber,
                         Text = s.AccountNumber + " " + s.AccountName
                     })
                     .ToListAsync(cancellationToken);
                 TempData["error"] = ex.Message;
                 return View(existingModel);
                }
            }
            else
            {
                TempData["error"] = "The information you submitted is not valid!";
                existingModel.Customers = await _dbContext.Customers
                    .OrderBy(c => c.CustomerId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.CustomerId.ToString(),
                        Text = s.CustomerName
                    })
                    .ToListAsync(cancellationToken);

                existingModel.SalesInvoices = await _dbContext.SalesInvoices
                    .Where(si => !si.IsPaid && si.CustomerId == existingModel.CustomerId)
                    .OrderBy(si => si.SalesInvoiceId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.SalesInvoiceId.ToString(),
                        Text = s.SalesInvoiceNo
                    })
                    .ToListAsync(cancellationToken);

                existingModel.ChartOfAccounts = await _dbContext.ChartOfAccounts
                    .Where(coa => !coa.HasChildren)
                    .OrderBy(coa => coa.AccountId)
                    .Select(s => new SelectListItem
                    {
                        Value = s.AccountNumber,
                        Text = s.AccountNumber + " " + s.AccountName
                    })
                    .ToListAsync(cancellationToken);
                return View(existingModel);
            }
        }

        public async Task<IActionResult> Post(int id, CancellationToken cancellationToken)
        {
            var model = await _receiptRepo.FindCR(id, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var collectionPrint = model.MultipleSIId != null ? nameof(MultipleCollectionPrint) : nameof(CollectionPrint);
            var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
            var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.PostedDate : DateTime.Now;
            try
            {
                if (!model.IsPosted)
                {
                    model.IsPosted = true;
                    model.PostedBy = createdBy;
                    model.PostedDate = date;

                    List<Offsetting>? offset;
                    decimal offsetAmount = 0;

                    if (model.SalesInvoiceId != null)
                    {
                        offset = await _receiptRepo.GetOffsettingAsync(model.CollectionReceiptNo!, model.SINo!, cancellationToken);
                        if (offset.Any())
                        {
                            offsetAmount = offset.Sum(o => o.Amount);
                        }
                    }
                    else
                    {
                        offset = await _receiptRepo.GetOffsettingAsync(model.CollectionReceiptNo!, model.SVNo!, cancellationToken);
                        if (offset.Any())
                        {
                            offsetAmount = offset.Sum(o => o.Amount);
                        }
                    }

                    await _receiptRepo.PostAsync(model, offset, cancellationToken);

                    if (model.SalesInvoiceId != null)
                    {
                        await _receiptRepo.UpdateInvoice(model.SalesInvoice!.SalesInvoiceId, model.Total, offsetAmount, cancellationToken);
                    }
                    else if (model.MultipleSIId != null)
                    {
                        await _receiptRepo.UpdateMultipleInvoice(model.MultipleSI!, model.SIMultipleAmount!, offsetAmount, cancellationToken);
                    }
                    else
                    {
                        await _receiptRepo.UpdateSv(model.ServiceInvoice!.ServiceInvoiceId, model.Total, offsetAmount, cancellationToken);
                    }

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Posted collection receipt# {model.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Collection Receipt has been Posted.";
                }
                return RedirectToAction(collectionPrint, new { id });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(collectionPrint, new { id });
            }
        }

        public async Task<IActionResult> Void(int id, CancellationToken cancellationToken)
        {
            var model = await _receiptRepo.FindCR(id, cancellationToken);

            if (!model.IsVoided)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var createdBy = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedBy : await _generalRepo.GetUserFullNameAsync(User.Identity!.Name!);
                var date = !model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId != 0 ? model.VoidedDate : DateTime.Now;
                try
                {
                    if (model.IsPosted)
                    {
                        model.IsPosted = false;
                    }

                    model.IsVoided = true;
                    model.VoidedBy = createdBy;
                    model.VoidedDate = date;
                    var series = model.SINo ?? model.SVNo;

                    var findOffsetting = await _dbContext.Offsettings.Where(offset => offset.Source == model.CollectionReceiptNo && offset.Reference == series).ToListAsync(cancellationToken);

                    await _generalRepo.RemoveRecords<CashReceiptBook>(crb => crb.RefNo == model.CollectionReceiptNo, cancellationToken);
                    await _generalRepo.RemoveRecords<GeneralLedgerBook>(gl => gl.Reference == model.CollectionReceiptNo, cancellationToken);

                    if (findOffsetting.Any())
                    {
                        await _generalRepo.RemoveRecords<Offsetting>(offset => offset.Source == model.CollectionReceiptNo && offset.Reference == series, cancellationToken);
                    }
                    if (model.SINo != null)
                    {
                        await _receiptRepo.RemoveSIPayment(model.SalesInvoice!.SalesInvoiceId, model.Total, findOffsetting.Sum(offset => offset.Amount), cancellationToken);
                    }
                    else if (model.SVNo != null)
                    {
                        await _receiptRepo.RemoveSVPayment(model.ServiceInvoiceId, model.Total, findOffsetting.Sum(offset => offset.Amount), cancellationToken);
                    }
                    else if (model.MultipleSI != null)
                    {
                        await _receiptRepo.RemoveMultipleSIPayment(model.MultipleSIId!, model.SIMultipleAmount!, findOffsetting.Sum(offset => offset.Amount), cancellationToken);
                    }
                    else
                    {
                        TempData["error"] = "No series number found";
                        return RedirectToAction(nameof(CollectionIndex));
                    }

                    #region --Audit Trail Recording

                    if (model.OriginalSeriesNumber.IsNullOrEmpty() && model.OriginalDocumentId == 0)
                    {
                        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                        AuditTrail auditTrailBook = new(createdBy, $"Voided collection receipt# {model.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                        await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                    }

                    #endregion --Audit Trail Recording

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    TempData["success"] = "Collection Receipt has been Voided.";

                    return RedirectToAction(nameof(CollectionIndex));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    TempData["error"] = ex.Message;
                }
            }

            return NotFound();
        }

        public async Task<IActionResult> Cancel(int id, string cancellationRemarks, CancellationToken cancellationToken)
        {
            var model = await _dbContext.CollectionReceipts.FirstOrDefaultAsync(x => x.CollectionReceiptId == id, cancellationToken);
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
                            AuditTrail auditTrailBook = new(createdBy, $"Cancelled collection receipt# {model.CollectionReceiptNo}", "Collection Receipt", ipAddress!);
                            await _dbContext.AddAsync(auditTrailBook, cancellationToken);
                        }

                        #endregion --Audit Trail Recording

                        await _dbContext.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        TempData["success"] = "Collection Receipt has been Cancelled.";
                    }
                    return RedirectToAction(nameof(CollectionIndex));
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["error"] = ex.Message;
                return RedirectToAction(nameof(CollectionIndex));
            }

            return NotFound();
        }
    }
}
