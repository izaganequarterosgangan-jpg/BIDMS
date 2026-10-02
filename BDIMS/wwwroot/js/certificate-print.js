/*
 * Shared certificate printing.
 *
 * Certificates are printed from a self-contained pop-up window rather than by hiding
 * page chrome with a @media print block. The server already renders the full layout
 * (RenderCertificateHtml / PreviewCertificateTemplate), so the same markup can be
 * reused verbatim for the on-screen preview, the pop-out and the printed sheet — which
 * guarantees that what staff approve is what comes out of the printer.
 */
window.BdimsCertificates = (function () {
    'use strict';

    var PRINT_CSS = [
        '@page { size: A4 portrait; margin: 10mm; }',
        'html, body { margin: 0; padding: 0; background: #f1f5f9; }',
        'body {',
        '    font-family: "Times New Roman", Times, serif;',
        '    color: #000;',
        '    -webkit-print-color-adjust: exact;',
        '    print-color-adjust: exact;',
        '}',
        '#print-sheet {',
        '    background: #fff;',
        '    position: relative;',
        '    z-index: 0;',
        '    width: 210mm;',
        '    height: 297mm;',
        '    margin: 0 auto;',
        '    padding: 12mm 14mm;',
        '    box-sizing: border-box;',
        '    box-shadow: 0 2px 8px rgba(0,0,0,.15);',
        '}',
        '/* A "Behind text" logo is placed with position:absolute; z-index:-1, and an',
        '   opaque in-flow wrapper paints on top of negative-z descendants. The sheet is',
        '   already white, so the wrapper is made transparent instead. */',
        '#print-sheet .certificate-container { background: transparent !important; }',
        '#print-sheet img { max-width: 100%; height: auto; }',
        '#print-sheet table { max-width: 100%; }',
        '#print-sheet h1, #print-sheet h2, #print-sheet h3 { text-align: center; }',
        'img[src=""], img:not([src]) { display: none; }',
        '@media print {',
        '    html, body { background: #fff; }',
        '    /* The 10mm @page margins leave 277mm of printable height. A definite height',
        '       is still required for the percentage positioning of placed logos. */',
        '    #print-sheet { box-shadow: none; margin: 0; width: auto; height: 277mm; padding: 0; }',
        '}'
    ].join('\n');

    /**
     * Opens the print window up front, so callers that have to fetch the rendered
     * certificate first can still open it from inside the user gesture. Browsers
     * block window.open() from an async continuation, so opening it later would
     * silently fail. Pass the result to printHtml().
     */
    function openPrintWindow() {
        return window.open('', '_blank', 'width=980,height=760');
    }

    /**
     * Opens a print window containing the given already-rendered certificate markup.
     * Resolves to true when the window opened, false when the pop-up was blocked.
     *
     * @param {string} html  Rendered certificate markup.
     * @param {string} title Document title for the pop-up.
     * @param {Window} [targetWindow] Window from openPrintWindow(), when the markup
     *                          had to be fetched after the click.
     */
    function printHtml(html, title, targetWindow, certId) {
        var win = targetWindow || openPrintWindow();

        if (!win || win.closed) {
            return false;
        }

        var safeTitle = String(title || 'Certificate')
            .replace(/[&<>"]/g, function (c) {
                return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
            });

        // Certificate id forwarded to the pop-up so its own onafterprint can tell
        // the opener exactly which row to mark Printed. Escaped for both the
        // property assignment below and the injected <script> string.
        var rawCertId = String((certId === undefined || certId === null) ? '' : certId);
        var jsCertId = rawCertId.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/</g, '\\x3c').replace(/>/g, '\\x3e').replace(/\r?\n/g, ' ');

        try { win.__bdimsCertId = rawCertId; } catch (e) { /* cross-window guard */ }

        // Backup handler set from the opener side. The injected in-popup script
        // below is the primary path (it runs even if this assignment is lost on
        // navigation); both do the same thing: notify the opener, then close.
        try {
            win.onafterprint = function () {
                try {
                    if (window.BdimsPrintDone) window.BdimsPrintDone(rawCertId);
                } catch (e2) { /* never break printing */ }
                try { if (!win.closed) win.close(); } catch (e3) { /* ignore */ }
            };
        } catch (e) { /* older browsers without onafterprint */ }

        win.document.open();
        win.document.write(
            '<!DOCTYPE html><html><head><meta charset="utf-8" />' +
            '<title>' + safeTitle + '</title>' +
            '<style>' + PRINT_CSS + '</style>' +
            '</head><body><div id="print-sheet">' + html + '</div>' +
            '<script>window.__bdimsCertId="' + jsCertId + '";' +
            'window.onafterprint=function(){' +
            'try{if(window.opener&&!window.opener.closed&&window.opener.BdimsPrintDone){window.opener.BdimsPrintDone(window.__bdimsCertId||"");}}catch(e){}' +
            'window.close();' +
            '};<' + '/script></body></html>');
        win.document.close();
        win.focus();

        // Give inline data-URI images a moment to decode, otherwise Firefox can print
        // a half-laid-out sheet.
        win.setTimeout(function () {
            win.print();
        }, 350);

        return true;
    }

    return {
        openPrintWindow: openPrintWindow,
        printHtml: printHtml,
        printCss: PRINT_CSS
    };
})();
