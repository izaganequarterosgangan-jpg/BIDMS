// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
// Notifications helper: fetch list and show in modal
function openNotifications() {
    fetch('/Home/GetNotifications')
        .then(function (r) { return r.json(); })
        .then(function (data) {
            if (!data || data.length === 0) {
                openModal('Notifications', '<p class="text-muted">No notifications at this time.</p>');
                return;
            }

            var html = '<div class="list-group">';
            data.forEach(function (n) {
                html += '<a class="list-group-item list-group-item-action" href="/Home/Announcements">' +
                    '<div class="d-flex w-100 justify-content-between"><h6 class="mb-1">' + escapeHtml(n.title) + '</h6><small>' + escapeHtml(n.date) + '</small></div>' +
                    '<p class="mb-1 text-truncate">' + escapeHtml(n.purpose) + '</p>' +
                    '</a>';
            });
            html += '</div>';

            openModal('Notifications', html);
        })
        .catch(function (err) {
            openModal('Notifications', '<p class="text-danger">Unable to load notifications.</p>');
            console.error(err);
        });
}

// ------- Shared notification dropdown logic -------
function getAntiforgeryToken() {
    var el = document.querySelector('input[name="__RequestVerificationToken"]') ||
             document.querySelector('meta[name="csrf-token"]');
    return el ? (el.value || el.getAttribute('content') || '') : '';
}

function postJson(url, body) {
    return fetch(url, {
        method: 'POST',
        headers: {
            'X-Requested-With': 'XMLHttpRequest',
            'Content-Type': 'application/x-www-form-urlencoded',
            'RequestVerificationToken': getAntiforgeryToken()
        },
        body: body || ''
    });
}

function updateNotifBadge(count) {
    var els = document.querySelectorAll('.notif-badge');
    els.forEach(function (el) {
        if (!el) return;
        if (!count || count <= 0) {
            // Hide rather than remove: the span is server-rendered on some pages, so it
            // has to stay in the DOM for the next update to write into.
            el.style.display = 'none';
            el.textContent = '0';

            // Cleared alongside the number so a stale tooltip like "3 unread" can never
            // survive on a bell whose badge is hidden.
            if (el.hasAttribute('title')) el.setAttribute('title', '0 unread notifications');
            if (el.hasAttribute('aria-label')) el.setAttribute('aria-label', '0 unread notifications');
        } else {
            el.style.display = 'inline-flex';
            el.textContent = (count > 99) ? '99+' : String(count);
            el.setAttribute('title', count + ' unread notification' + (count === 1 ? '' : 's'));
            el.setAttribute('aria-label', count + ' unread notification' + (count === 1 ? '' : 's'));
        }
    });
}

function fetchUnreadCount() {
    fetch('/Home/GetUnreadCount')
        .then(function (r) { return r.json(); })
        .then(function (data) {
            updateNotifBadge(data.count || 0);
        })
        .catch(function (err) { console.error('Unread count fetch failed', err); });
}

function toggleNotifDropdown(e) {
    // Ensure only one dropdown visible
    var dropdown = document.getElementById('notifDropdown');
    if (!dropdown) return;
    if (dropdown.style.display === 'block') {
        dropdown.style.display = 'none';
        dropdown.setAttribute('aria-hidden', 'true');
        return;
    }

    // load notifications and show
    fetch('/Home/GetNotifications')
        .then(function (r) { return r.json(); })
        .then(function (items) {
            renderNotifDropdown(items || []);
            dropdown.style.display = 'block';
            dropdown.setAttribute('aria-hidden', 'false');
        })
        .catch(function (err) {
            console.error('Failed to load notifications', err);
            dropdown.innerHTML = '<div class="notif-empty">Unable to load notifications.</div>';
            dropdown.style.display = 'block';
        });

    // close when clicking outside
    setTimeout(function () {
        document.addEventListener('click', outsideClickListener);
    }, 10);

    function outsideClickListener(ev) {
        var wrapper = ev.target.closest('.notification-wrapper');
        if (!wrapper) {
            var dd = document.getElementById('notifDropdown');
            if (dd) { dd.style.display = 'none'; dd.setAttribute('aria-hidden', 'true'); }
            document.removeEventListener('click', outsideClickListener);
        }
    }
}

function renderNotifDropdown(items) {
    var dropdown = document.getElementById('notifDropdown');
    if (!dropdown) return;

    var html = '<div class="header"><strong>Notifications</strong><button class="btn btn-link" onclick="markAllRead(event)">Mark all as read</button></div>';
    if (!items || items.length === 0) {
        html += '<div class="notif-empty">No notifications at this time.</div>';
        dropdown.innerHTML = html;
        updateNotifBadge(0);
        return;
    }

    html += '<div class="items">';
    items.forEach(function (n) {
        var cls = n.isRead ? 'notif-item read' : 'notif-item unread';
        html += '<div class="' + cls + '" data-id="' + escapeHtml(n.id) + '"' +
            ' data-type="' + escapeHtml(n.type || '') + '"' +
            ' data-related-url="' + escapeHtml(n.relatedUrl || '') + '">';
        html += '<div class="notif-item-content">';
        html += '<div class="title">' + escapeHtml(n.title) + '</div>';
        html += '<div class="meta">' + escapeHtml(n.message || '') + '</div>';
        html += '<div class="meta">' + escapeHtml(n.createdAt || '') + '</div>';
        html += '</div>';
        html += '<button type="button" class="notif-view-btn" data-id="' + escapeHtml(n.id) + '" title="View announcement details" aria-label="View announcement details"><i class="bi bi-eye"></i></button>';
        html += '</div>';
    });
    html += '</div>';

    dropdown.innerHTML = html;

    // update badge count
    var unread = items.filter(function (i) { return !i.isRead; }).length;
    updateNotifBadge(unread);
}

function handleNotifClick(ev, id, type, relatedUrl) {
    ev.stopPropagation();

    var itemEl = null;
    if (id === null || id === undefined) {
        var clicked = ev.target.closest('.notif-item');
        if (!clicked) return;
        itemEl = clicked;
        id = clicked.getAttribute('data-id');
        type = clicked.getAttribute('data-type');
        relatedUrl = clicked.getAttribute('data-related-url');
    } else {
        itemEl = ev.target.closest ? ev.target.closest('.notif-item') : null;
    }
    if (!id) return;

    // Only an item that is currently unread counts towards the badge. Decrementing
    // blindly would drive the count negative (and then hide a badge that should still
    // show) when an already-read item is clicked.
    var wasUnread = !!(itemEl && itemEl.classList.contains('unread'));

    // Immediate visual feedback: flip this row to its read appearance and step the
    // badge down by one before the request completes. renderNotifDropdown() below
    // re-applies the state from the server, which corrects it if the save failed.
    if (wasUnread) {
        itemEl.classList.remove('unread');
        itemEl.classList.add('read');

        var current = parseInt(document.querySelector('.notif-badge').textContent, 10);
        if (!isNaN(current) && current > 0) {
            updateNotifBadge(current - 1);
        } else {
            updateNotifBadge(0);
        }
    }

    // Mark as read via POST
    postJson('/Home/MarkAsRead', 'id=' + encodeURIComponent(id))
    .then(function (r) {
        if (r.status !== 200) throw new Error('MarkAsRead failed with status ' + r.status);
        return r.json();
    })
    .then(function (data) {
        // Server count wins over the optimistic one.
        updateNotifBadge(data && typeof data.unread === 'number' ? data.unread : 0);

        // Refresh the list so the read state on screen matches the database.
        return fetch('/Home/GetNotifications')
            .then(function (r) { return r.json(); })
            .then(function (items) { renderNotifDropdown(items || []); });
    })
    .then(function () {
        // show full announcement details
        openNotificationDetails(id);
    })
    .catch(function (err) {
        console.error(err);
        // Re-sync from the server rather than leaving a badge that may be wrong.
        fetchUnreadCount();
    });
}

function openNotificationDetails(id) {
    fetch('/Home/GetNotification?id=' + encodeURIComponent(id))
        .then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        })
        .then(function (d) {
            if (!d || !d.success) { showToast('Unable to load notification details.', 'danger'); return; }
            renderNotificationDetails(d);
        })
        .catch(function (err) {
            console.error(err);
            showToast('Unable to load notification details.', 'danger');
        });
}

function priorityBadgeClass(priority) {
    var p = String(priority || '').toLowerCase();
    if (p === 'high') return 'text-bg-danger';
    if (p === 'medium') return 'text-bg-warning';
    return 'text-bg-success';
}

function renderNotificationDetails(d) {
    var body = document.getElementById('notificationDetailsBody');
    if (!body) {
        // Fallback when the shared modal is not present
        var fallback =
            '<h5 class="fw-bold mb-2">' + escapeHtml(d.announcementTitle) + '</h5>' +
            '<p class="text-muted mb-2"><strong>Priority:</strong> ' + escapeHtml(d.priority) + '</p>' +
            '<p class="text-muted mb-2"><strong>Target Audience:</strong> ' + escapeHtml(d.targetAudience) + '</p>' +
            '<p class="text-muted mb-2"><strong>Date:</strong> ' + escapeHtml(d.date) + '</p>' +
            '<hr/><p>' + escapeHtml(d.purposeAndDetails) + '</p>';
        openModal('Announcement Details', fallback);
        return;
    }

    var html =
        '<h4 class="fw-bold mb-2">' + escapeHtml(d.announcementTitle) + '</h4>' +
        '<div class="mb-3">' +
            '<span class="badge ' + priorityBadgeClass(d.priority) + ' me-2">' + escapeHtml(d.priority) + ' Priority</span>' +
            '<span class="badge text-bg-secondary">Audience: ' + escapeHtml(d.targetAudience) + '</span>' +
        '</div>' +
        '<div class="mb-3">' +
            '<small class="text-muted d-block">Date</small>' +
            '<strong>' + escapeHtml(d.date) + '</strong>' +
        '</div>' +
        '<hr/>' +
        '<div>' +
            '<h6 class="fw-semibold">Purpose & Details</h6>' +
            '<p class="mb-0">' + escapeHtml(d.purposeAndDetails) + '</p>' +
        '</div>';

    if (d.relatedUrl && d.relatedUrl.length > 0) {
        html += '<div class="mt-3 pt-3 border-top text-end"><a href="' + escapeHtml(d.relatedUrl) + '" class="btn btn-sm btn-outline-primary">View in Announcements</a></div>';
    }

    body.innerHTML = html;

    var modalEl = document.getElementById('notificationDetailsModal');
    var modal = bootstrap.Modal.getInstance(modalEl);
    if (modal) modal.show();
    else { modal = new bootstrap.Modal(modalEl); modal.show(); }
}

function markAllRead(e) {
    if (e) e.stopPropagation();

    // Optimistic update: the badge and every unread row are switched to their read
    // appearance straight away, so the UI reacts on the same click instead of waiting
    // for the round-trip. renderNotifDropdown() re-applies this from the server
    // response, which keeps it correct if the request fails.
    var dropdown = document.getElementById('notifDropdown');
    if (dropdown) {
        dropdown.querySelectorAll('.notif-item.unread').forEach(function (item) {
            item.classList.remove('unread');
            item.classList.add('read');
        });
    }
    updateNotifBadge(0);

    postJson('/Home/MarkAllRead', '')
    .then(function (r) {
        if (r.status !== 200) throw new Error('Mark all read failed with status ' + r.status);
        return r.json();
    })
    .then(function (data) {
        // The server count is authoritative. Falling back to 0 when the field is absent
        // keeps an older build from leaving a stale number on screen.
        updateNotifBadge(data && typeof data.unread === 'number' ? data.unread : 0);

        return fetch('/Home/GetNotifications')
            .then(function (r) { return r.json(); })
            .then(function (items) { renderNotifDropdown(items || []); });
    })
    .catch(function (err) {
        console.error('Mark all read failed', err);
        // Roll back to the server truth so a failed save cannot leave the badge hidden
        // while the notifications are still unread.
        fetchUnreadCount();
    });
}

// ensure unread count is fetched when page loads, and wire up notification item clicks
document.addEventListener('DOMContentLoaded', function () {
    fetchUnreadCount();

    document.addEventListener('click', function (ev) {
        var viewBtn = ev.target.closest('.notif-view-btn');
        if (viewBtn) {
            ev.stopPropagation();
            var vbItem = viewBtn.closest('.notif-item');
            handleNotifClick(ev, viewBtn.getAttribute('data-id'),
                vbItem ? vbItem.getAttribute('data-type') : null,
                vbItem ? vbItem.getAttribute('data-related-url') : null);
            return;
        }

        var item = ev.target.closest('.notif-item');
        if (!item) return;
        handleNotifClick(ev,
            item.getAttribute('data-id'),
            item.getAttribute('data-type'),
            item.getAttribute('data-related-url'));
    });

    document.addEventListener('click', function (ev) {
        var del = ev.target.closest('.announcement-delete-btn');
        if (!del) return;
        ev.preventDefault();
        if (!confirm('Delete this announcement?')) return;
        var id = del.getAttribute('data-id');
        fetch('/Home/DeleteAnnouncement', {
            method: 'POST',
            headers: {
                'X-Requested-With': 'XMLHttpRequest',
                'Content-Type': 'application/x-www-form-urlencoded',
                'RequestVerificationToken': getAntiforgeryToken()
            },
            body: 'id=' + encodeURIComponent(id)
        })
        // fetch resolves on 4xx and 5xx too, so without checking ok an antiforgery
        // rejection or a server error still navigated to the list and the row was
        // still there with no error shown and .catch never reached.
        .then(function (response) {
            if (!response.ok) {
                throw new Error('Delete failed with status ' + response.status);
            }
            return response.json().catch(function () { return {}; });
        })
        .then(function (result) {
            if (result && result.success === false) {
                showToast(result.message || 'Announcement was not found.', 'warning');
                return;
            }
            window.location = '/Home/Announcements';
        })
        .catch(function (err) {
            console.error(err);
            showToast('Unable to delete announcement.', 'danger');
        });
    });
});

// lucide replaces each <i data-lucide="..."> with an inline <svg> exactly once. Rows
// inserted later contain raw <i data-lucide> placeholders, so they have to be
// re-rendered or their icons show up as empty boxes.
function refreshIcons() {
    if (typeof lucide !== 'undefined' && typeof lucide.createIcons === 'function') {
        lucide.createIcons();
    }
}

function escapeHtml(unsafe) {
    if (unsafe === null || unsafe === undefined) return '';
    return String(unsafe).replace(/[&<>"'`]/g, function (m) {
        return ({
            '&': '&amp;',
            '<': '&lt;',
            '>': '&gt;',
            '"': '&quot;',
            "'": '&#39;',
            '`': '&#96;'
        })[m];
    });
}

// Announcements: handle AJAX submit for the create form
document.addEventListener('DOMContentLoaded', function () {
    var form = document.getElementById('announcementForm');
    if (!form) return;

    // Announcements.cshtml ships its own submit handler that also updates the summary
    // cards and the table in place. Two handlers bound to one form meant two POSTs per
    // click, which wrote a duplicate announcement row every time. Whichever script runs
    // first claims the form with this flag; the other one stands down, so exactly one
    // handler is ever attached.
    if (form.getAttribute('data-submit-bound') === '1') return;
    form.setAttribute('data-submit-bound', '1');

    form.addEventListener('submit', function (e) {
        e.preventDefault();

        if (form.getAttribute('data-submitting') === '1') { return; }
        form.setAttribute('data-submitting', '1');

        var submitBtn = form.querySelector('button[type="submit"]');
        if (submitBtn) { submitBtn.disabled = true; }

        var formData = new FormData(form);

        fetch(form.action, {
            method: 'POST',
            headers: {
                'X-Requested-With': 'XMLHttpRequest'
            },
            body: formData
        })
        .then(function (r) { return r.json(); })
        .then(function (data) {
            if (!data || !data.success) {
                // show simple alert on failure
                showToast('Error creating announcement.', 'danger');
                return;
            }

            // Insert new row into table
            var tbody = document.querySelector('.custom-table tbody');
            if (tbody) {
                var item = data.item;
                var tr = document.createElement('tr');

                tr.innerHTML = "<td class=\"post-title\">" + escapeHtml(item.title) + "</td>" +
                    "<td class=\"post-purpose\">" + escapeHtml(item.purpose) + "</td>" +
                    "<td><span class=\"audience-badge\">" + escapeHtml(item.targetAudience) + "</span></td>" +
                    "<td class=\"post-date\">" + escapeHtml(item.date) + "</td>" +
                    "<td><span class=\"priority-tag priority-" + escapeHtml(String(item.priority || '').toLowerCase()) + "\">" + escapeHtml(item.priority) + "</span></td>" +
                    "<td><button type=\"button\" class=\"announcement-delete-btn\" data-id=\"" + escapeHtml(item.id) + "\" " +
                    "style=\"background:none; border:none; color:#ef4444; cursor:pointer;\" title=\"Delete\">" +
                    "<i data-lucide=\"trash-2\" style=\"width:16px; height:16px;\"></i></button></td>";

                // prepend new row
                if (tbody.firstChild) tbody.insertBefore(tr, tbody.firstChild);
                else tbody.appendChild(tr);

                // The new row's <i data-lucide> placeholder only becomes an icon when
                // createIcons() runs again; otherwise its delete button is a blank box.
                refreshIcons();
            }

            // close modal
            var modalEl = document.getElementById('announcementModalBootstrap');
            if (modalEl) {
                var modalInstance = bootstrap.Modal.getInstance(modalEl);
                if (modalInstance) modalInstance.hide();
            }

            showToast('Announcement created.', 'success');

            // refresh the notification bell badge + dropdown entries
            fetchUnreadCount();
            var dd = document.getElementById('notifDropdown');
            if (dd && dd.style.display === 'block') {
                fetch('/Home/GetNotifications').then(function (r) { return r.json(); }).then(function (items) { renderNotifDropdown(items || []); });
            }

            // reset form
            form.reset();
        })
        .catch(function (err) {
            console.error(err);
            showToast('Unable to create announcement.', 'danger');
        });
    });
});

// Helper to show toast notifications. Self-contained: creates its own fixed
// container when one is missing, never blocks the page, and works whether or
// not Bootstrap's JS bundle is available.
function showToast(message, type) {
    try {
        var toastWrapper = document.getElementById('toastWrapper');
        if (!toastWrapper) {
            toastWrapper = document.createElement('div');
            toastWrapper.id = 'toastWrapper';
            toastWrapper.className = 'toast-wrapper';
            toastWrapper.setAttribute('aria-live', 'polite');
            toastWrapper.style.cssText = 'position:fixed;bottom:20px;right:20px;z-index:1080;display:flex;flex-direction:column;gap:8px;';
            document.body.appendChild(toastWrapper);
        }

        var isSuccess = type === 'success';
        var toastEl = document.createElement('div');
        toastEl.className = 'toast show align-items-center text-white border-0';
        toastEl.setAttribute('role', 'alert');
        toastEl.setAttribute('aria-live', 'assertive');
        toastEl.setAttribute('aria-atomic', 'true');
        toastEl.style.cssText =
            'max-width:360px;padding:12px 14px;border-radius:8px;box-shadow:0 4px 12px rgba(0,0,0,.2);' +
            'display:flex;align-items:center;justify-content:space-between;gap:10px;background:' +
            (isSuccess ? '#198754' : '#dc3545') + ';opacity:0;transition:opacity .25s ease;';

        var body = document.createElement('div');
        body.innerHTML = escapeHtml(message);
        var close = document.createElement('button');
        close.type = 'button';
        close.setAttribute('aria-label', 'Close');
        close.innerHTML = '&times;';
        close.style.cssText = 'background:none;border:none;color:#fff;font-size:20px;line-height:1;cursor:pointer;';
        close.onclick = function () { toastEl.remove(); };
        toastEl.appendChild(body);
        toastEl.appendChild(close);
        toastWrapper.appendChild(toastEl);

        requestAnimationFrame(function () { toastEl.style.opacity = '1'; });
        var hide = function () {
            toastEl.style.opacity = '0';
            setTimeout(function () { if (toastEl.parentNode) toastEl.parentNode.removeChild(toastEl); }, 250);
        };
        setTimeout(hide, 4000);
    } catch (e) {
        // Never let toast rendering break the calling flow
        console.error('showToast failed:', e);
    }
}
