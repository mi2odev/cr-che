using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.Input;
using Creche.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace Creche.App.Services;

/// <summary>
/// Printing through WPF's own stack: FlowDocument → DocumentPaginator → PrintDialog / System.Printing queue.
/// "Export to PDF" sends the same document to the built-in "Microsoft Print to PDF" printer.
/// </summary>
public sealed class PrintService(DialogService dialogs, ToastService toasts, ILogger<PrintService> log)
{
    /// <summary>Shows a preview with Print and PDF buttons. <paramref name="build"/> is called again for each output.</summary>
    public void Preview(string title, Func<FlowDocument> build) => dialogs.Show(new PrintPreviewViewModel(title, build, this));

    public bool Print(string title, Func<FlowDocument> build, bool toPdf)
    {
        try
        {
            var dialog = new PrintDialog();
            var pdfQueue = toPdf ? FindPdfQueue() : null;
            if (pdfQueue is not null)
            {
                dialog.PrintQueue = pdfQueue;
                dialog.PrintTicket = pdfQueue.DefaultPrintTicket;
            }
            else
            {
                if (toPdf) toasts.Warning(Loc.T("Print.PdfPrinterMissing"));
                if (dialog.ShowDialog() != true) return false;
            }

            var document = build();
            bool a5 = document.PageWidth < 600;
            try
            {
                dialog.PrintTicket.PageMediaSize = new PageMediaSize(a5 ? PageMediaSizeName.ISOA5 : PageMediaSizeName.ISOA4);
                dialog.PrintTicket.PageOrientation = PageOrientation.Portrait;
            }
            catch (Exception ex) when (ex is PrintSystemException or ArgumentException or InvalidOperationException)
            {
                log.LogDebug(ex, "The printer did not accept the page size");
            }

            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.PageSize = new Size(document.PageWidth, document.PageHeight);
            // The PDF printer asks where to save the file itself.
            dialog.PrintDocument(paginator, title);
            return true;
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            log.LogError(ex, "Printing '{Title}' failed", title);
            toasts.Error(Loc.T("Print.Failed", ex.Message));
            return false;
        }
    }

    private static PrintQueue? FindPdfQueue()
    {
        try
        {
            var queues = new LocalPrintServer().GetPrintQueues([EnumeratedPrintQueueTypes.Local]).ToList();
            return queues.FirstOrDefault(q => q.Name.Contains("Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase))
                   ?? queues.FirstOrDefault(q => q.Name.Contains("PDF", StringComparison.OrdinalIgnoreCase));
        }
        catch (PrintSystemException)
        {
            return null;
        }
    }
}

public sealed partial class PrintPreviewViewModel : DialogViewModel
{
    private readonly Func<FlowDocument> _build;
    private readonly PrintService _printer;

    public PrintPreviewViewModel(string title, Func<FlowDocument> build, PrintService printer)
    {
        Title = title;
        _build = build;
        _printer = printer;
        Document = build();
    }

    public FlowDocument Document { get; }

    [RelayCommand]
    private void Print()
    {
        if (_printer.Print(Title, _build, toPdf: false)) Close(true);
    }

    [RelayCommand]
    private void Pdf()
    {
        if (_printer.Print(Title, _build, toPdf: true)) Close(true);
    }
}
