// Certificates page behaviour.
//
// Lives in a static file rather than an inline <script> block in Certificates.cshtml.
// A stray "</script>" anywhere in an inline block - in a string literal, or in data
// interpolated into one - ends the element early and dumps the remaining JavaScript
// onto the page as visible text under Recent Certificates. Keeping the code in a
// file the browser fetches removes that whole failure mode.
//
// Everything is scoped inside the IIFE and every event is delegated, so nothing here
// is published on window and the table needs no inline onclick attributes.
        (function () {
            'use strict';

            // Last successfully rendered markup, so "Print Document" reprints exactly
            // what the clerk just approved in the preview.
            var currentCertHtml = '';
            var currentCertTitle = '';
            var currentCertId = '';

            var residentDirectory = readJson('certResidentsJson', []);

            function readJson(elementId, fallback) {
                var el = document.getElementById(elementId);
                if (!el) {
                    return fallback;
                }
                try {
                    return JSON.parse(el.textContent || '[]');
                } catch (e) {
                    return fallback;
                }
            }

            function field(id) {
                return document.getElementById(id);
            }

            function certFromBtn(btn) {
                return {
                    title: btn.getAttribute('data-title') || '',
                    resident: btn.getAttribute('data-resident') || '',
                    residentAge: btn.getAttribute('data-age') || '',
                    purok: btn.getAttribute('data-purok') || '',
                    address: btn.getAttribute('data-address') || '',
                    purpose: btn.getAttribute('data-purpose') || '',
                    issued: btn.getAttribute('data-issued') || '',
                    orNumber: btn.getAttribute('data-ornumber') || '',
                    amountPaid: btn.getAttribute('data-amount') || '',
                    certificateId: btn.getAttribute('data-id') || ''
                };
            }

            // The server owns placeholder replacement, so every {{ Token }} the template
            // editor supports is filled from the same engine the preview and the printed
            // sheet use. Re-implementing the token map here is what left {{ ... }} visible
            // on printed certificates.
            function renderCertificate(data) {
                var query = new URLSearchParams();
                var params = {
                    title: data.title,
                    resident: data.resident,
                    residentAge: data.residentAge,
                    purok: data.purok,
                    address: data.address,
                    purpose: data.purpose,
                    orNumber: data.orNumber,
                    amountPaid: data.amountPaid,
                    issued: data.issued,
                    certificateId: data.certificateId
                };

                Object.keys(params).forEach(function (key) {
                    var value = params[key];
                    if (value !== null && value !== undefined && String(value).trim() !== '') {
                        query.set(key, value);
                    }
                });

                return fetch('/Home/RenderCertificateHtml?' + query.toString())
                    .then(function (r) {
                        if (!r.ok) {
                            throw new Error('Render failed with status ' + r.status);
                        }
                        return r.json();
                    })
                    .then(function (d) {
                        if (!d || !d.html) {
                            throw new Error('Render returned no markup');
                        }
                        return d.html;
                    });
            }

            function showModal(elementId) {
                var el = field(elementId);
                if (!el) {
                    return;
                }
                if (typeof window.bootstrap !== 'undefined' && bootstrap.Modal) {
                    bootstrap.Modal.getOrCreateInstance(el).show();
                } else {
                    el.style.display = 'block';
                }
            }

            // Delegated so the table needs no inline onclick and nothing has to be
            // published on window.
            document.addEventListener('click', function (e) {
                var btn = e.target.closest('button[data-action]');
                if (!btn) return;

                var action = btn.getAttribute('data-action');

                if (action === 'preview') {
                    previewCert(certFromBtn(btn));
                } else if (action === 'print') {
                    printCert(certFromBtn(btn));
                } else if (action === 'print-preview') {
                    printPreviewedCert();
                } else if (action === 'delete') {
                    deleteCert(btn);
                }
            });

            function antiforgeryToken() {
                var el = document.querySelector('#certActionToken input[name="__RequestVerificationToken"]');
                return el ? el.value : '';
            }

            function postAction(url, id) {
                var body = 'id=' + encodeURIComponent(id) +
                    '&__RequestVerificationToken=' + encodeURIComponent(antiforgeryToken());
                return fetch(url, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                        'X-Requested-With': 'XMLHttpRequest'
                    },
                    body: body
                }).then(function (r) { return r.json(); });
            }

            function statusPillClass(status) {
                if (status === 'Pending') return 'status-pending';
                if (status === 'Printed') return 'status-complete';
                return 'status-ready';
            }

            function refreshCounters(d) {
                if (!d) return;
                var cards = document.querySelectorAll('.stats-row .mini-card strong');
                if (cards.length >= 3) {
                    if (d.issuedThisWeekCount !== undefined) cards[0].textContent = d.issuedThisWeekCount;
                    if (d.awaitingPickupCount !== undefined) cards[1].textContent = d.awaitingPickupCount;
                    if (d.printedCount !== undefined) cards[2].textContent = d.printedCount;
                }
                var hero = document.querySelector('.hero-card h3');
                if (hero && d.issuedThisWeekCount !== undefined) {
                    hero.textContent = d.issuedThisWeekCount + ' certificates issued this week';
                }
            }

            function rowForId(id) {
                var rows = document.querySelectorAll('table.custom-table tbody tr');
                for (var i = 0; i < rows.length; i++) {
                    var btn = rows[i].querySelector('button[data-id]');
                    if (btn && btn.getAttribute('data-id') === id) return rows[i];
                }
                return null;
            }

            function setRowPrinted(id, status) {
                var tr = rowForId(id);
                if (!tr) return;
                var pill = tr.querySelector('.status-pill');
                if (pill) {
                    pill.textContent = status;
                    pill.className = 'status-pill ' + statusPillClass(status);
                }
                var btns = tr.querySelectorAll('button[data-action="print"], button[data-action="preview"]');
                for (var i = 0; i < btns.length; i++) {
                    var oldStatus = btns[i].getAttribute('data-status');
                    if (oldStatus !== null) btns[i].setAttribute('data-status', status);
                }
            }

            function notifyPrintedSaved() {
                try {
                    if (typeof showToast === 'function') {
                        showToast('Printed and Saved successfully', 'success');
                    } else if (typeof window.showToast === 'function') {
                        window.showToast('Printed and Saved successfully', 'success');
                    } else {
                        window.alert('Printed and Saved successfully');
                    }
                } catch (e) {
                    try { window.alert('Printed and Saved successfully'); } catch (e2) { /* ignore */ }
                }
            }

            function markPrinted(id, fromAfterPrint) {
                if (!id) return;
                postAction('/Home/MarkCertificatePrinted', id)
                    .then(function (d) {
                        if (d && d.success) {
                            setRowPrinted(d.id || id, d.status || 'Printed');
                            refreshCounters(d);
                            // Toast only once the print/save dialog has actually closed
                            // (afterprint path). The immediate fallback call skips it so
                            // the clerk is not told "saved" before choosing Save/Print.
                            if (fromAfterPrint) notifyPrintedSaved();
                        }
                    })
                    .catch(function () { /* status stays as-is; print already succeeded */ });
            }

            function deleteCert(btn) {
                var id = btn.getAttribute('data-id') || '';
                if (!id) return;
                if (!window.confirm('Are you sure you want to delete this certificate record?')) return;
                postAction('/Home/DeleteCertificate', id)
                    .then(function (d) {
                        if (d && d.success) {
                            var tr = rowForId(d.id || id);
                            if (tr && tr.parentNode) tr.parentNode.removeChild(tr);
                            refreshCounters(d);
                            var tbody = document.querySelector('table.custom-table tbody');
                            if (tbody && tbody.querySelectorAll('tr').length === 0) {
                                var empty = document.createElement('tr');
                                var cell = document.createElement('td');
                                cell.setAttribute('colspan', '7');
                                cell.className = 'text-center text-muted py-4';
                                cell.innerHTML = 'No records found. Click <strong>Issue Walk-in Certificate</strong> to add your first real certificate.';
                                empty.appendChild(cell);
                                tbody.appendChild(empty);
                            }
                        } else {
                            window.alert((d && d.message) || 'Unable to delete this certificate.');
                        }
                    })
                    .catch(function () { window.alert('Unable to delete this certificate.'); });
            }

            function on(id, eventName, handler) {
                var el = field(id);
                if (el) {
                    el.addEventListener(eventName, handler);
                }
            }

            function previewCert(data) {
                var container = field('modalPreviewContainer');

                currentCertHtml = '';
                currentCertTitle = data.title;
                currentCertId = data.certificateId || '';
                field('previewModalTitle').innerText = 'Preview - ' + data.title;
                container.innerHTML = '<p class="text-muted">Rendering certificate…</p>';
                showModal('previewModal');

                renderCertificate(data)
                    .then(function (html) {
                        currentCertHtml = html;
                        container.innerHTML = html;
                    })
                    .catch(function (err) {
                        container.innerHTML =
                            '<div class="alert alert-danger">Could not render this certificate: ' +
                            escapeHtml(err.message) + '</div>';
                    });
            }

            function printCert(data) {
                // Open the window inside the click handler: browsers block window.open()
                // from the fetch continuation below.
                var win = BdimsCertificates.openPrintWindow();
                if (!win) {
                    return;
                }

                renderCertificate(data)
                    .then(function (html) {
                        BdimsCertificates.printHtml(html, data.title, win, data.certificateId);
                        // Chain the status update after the print action is initiated,
                        // without changing the existing preview/printing behaviour.
                        // The pop-up's own onafterprint (see certificate-print.js) calls
                        // window.BdimsPrintDone -> markPrinted + toast, then closes
                        // itself. markPrinted here is the fallback for browsers that
                        // never fire afterprint.
                        markPrinted(data.certificateId);
                    })
                    .catch(function (err) {
                        win.document.open();
                        win.document.write(
                            '<!DOCTYPE html><html><head><meta charset="utf-8" />' +
                            '<title>Print failed</title></head><body>' +
                            '<p>Could not render this certificate: ' + escapeHtml(err.message) + '</p>' +
                            '</body></html>');
                        win.document.close();
                    });
            }

            function printPreviewedCert() {
                if (!currentCertHtml) {
                    return;
                }
                // Synchronous from the click, so no pop-up blocker interference.
                // printHtml(null target) opens its own window; the in-popup
                // onafterprint notifies window.BdimsPrintDone, which marks Printed
                // and toasts. markPrinted here covers browsers without afterprint.
                BdimsCertificates.printHtml(currentCertHtml, currentCertTitle, null, currentCertId);
                markPrinted(currentCertId);
            }

            // Called by the print pop-up's own onafterprint (certificate-print.js)
            // once the print / Save-as-PDF dialog is finished/closed: updates the
            // row + counters, toasts, and the pop-up closes itself via window.close().
            window.BdimsPrintDone = function (certId) {
                markPrinted(certId || currentCertId, true);
            };

            function fillFeeFromCertType() {
                var sel = field('formTitle');
                var feeField = field('formAmountPaid');
                if (!sel || !feeField) return;
                var opt = sel.options[sel.selectedIndex];
                if (opt && opt.dataset && opt.dataset.fee !== undefined) {
                    var fee = parseFloat(opt.dataset.fee);
                    if (!isNaN(fee)) feeField.value = fee.toFixed(2);
                }
            }

            function loadCertFormat() {
                var sel = field('formTitle');
                var wrap = field('formatPreviewWrap');
                var pre = field('formFormatPreview');
                if (!sel || !wrap || !pre) return;
                var title = sel.options[sel.selectedIndex].value;
                fetch('/Home/GetCertificateFormat?title=' + encodeURIComponent(title))
                    .then(function (r) { return r.json(); })
                    .then(function (d) {
                        if (d && d.success && d.format && d.format.trim()) {
                            pre.textContent = d.format;
                            wrap.style.display = 'block';
                        } else {
                            wrap.style.display = 'none';
                        }
                    })
                    .catch(function () { wrap.style.display = 'none'; });
            }

            // Mirrors the server-side IssueDate wording so the clerk sees the exact text
            // the placeholder will print.
            function ordinal(day) {
                var suffix = (day % 100 === 11 || day % 100 === 12 || day % 100 === 13)
                    ? 'th'
                    : ({ 1: 'st', 2: 'nd', 3: 'rd' }[day % 10] || 'th');
                return day + suffix;
            }

            function renderIssueDatePreview() {
                var el = field('issueDatePreview');
                var input = field('formIssueDate');
                if (!el) return;

                var parts = (input && input.value ? input.value : '').split('-');
                if (parts.length !== 3) {
                    el.textContent = '';
                    return;
                }

                var date = new Date(Number(parts[0]), Number(parts[1]) - 1, Number(parts[2]));
                el.textContent = 'Prints as: ' + ordinal(date.getDate()) + ' day of ' +
                    date.toLocaleString('en-US', { month: 'long' }).toUpperCase() + ', ' + date.getFullYear();
            }

            function selectPurok(value) {
                var purokField = field('formPurok');
                if (!purokField) return;

                for (var p = 0; p < purokField.options.length; p++) {
                    if (purokField.options[p].value === value) {
                        purokField.selectedIndex = p;
                        return;
                    }
                }
            }

            // Fills Age and Purok from the roster when the name matches one. The fields
            // stay editable so the clerk can correct a stale record.
            function applyResidentProfile() {
                var nameInput = field('formResident');
                if (!nameInput) return;

                var typed = (nameInput.value || '').trim().toLowerCase();
                if (!typed) return;

                var match = null;
                for (var i = 0; i < residentDirectory.length; i++) {
                    if (residentDirectory[i].name.toLowerCase() === typed) {
                        match = residentDirectory[i];
                        break;
                    }
                }
                if (!match) return;

                var ageField = field('formResidentAge');
                if (ageField && match.age) {
                    ageField.value = match.age;
                }

                if (match.purok) {
                    selectPurok(match.purok);
                }
            }

            document.addEventListener('DOMContentLoaded', function () {
                on('formResident', 'input', applyResidentProfile);
                on('formIssueDate', 'change', renderIssueDatePreview);
                on('formTitle', 'change', function () {
                    fillFeeFromCertType();
                    loadCertFormat();
                });

                fillFeeFromCertType();
                loadCertFormat();
                renderIssueDatePreview();

                // Bridge from Documents: "Issue Certificate" opens this modal pre-filled
                var prefill = readJson('certPrefillJson', {});

                if (!prefill.open) {
                    return;
                }

                if (prefill.resident) field('formResident').value = prefill.resident;
                if (prefill.purpose) field('formPurpose').value = prefill.purpose;

                if (prefill.title) {
                    var fTitle = field('formTitle');
                    if (fTitle) {
                        var matched = false;
                        for (var i = 0; i < fTitle.options.length; i++) {
                            if (fTitle.options[i].value === prefill.title) {
                                fTitle.selectedIndex = i;
                                matched = true;
                                break;
                            }
                        }
                        if (!matched) fTitle.selectedIndex = 0;
                    }
                }

                fillFeeFromCertType();
                loadCertFormat();

                // The prefilled resident may already be on the roster, so the
                // age and purok placeholders resolve without retyping.
                applyResidentProfile();

                showModal('issueModal');
            });
        })();
