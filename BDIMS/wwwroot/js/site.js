// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
// Notifications helper: fetch list and show in modal
function openNotifications() {
    fetch(notificationEndpoint('GetNotifications'))
        .then(function (r) { return r.json(); })
        .then(function (data) {
            if (!data || data.length === 0) {
                openModal('Notifications', '<p class="text-muted">No notifications at this time.</p>');
                return;
            }

            var html = '<div class="list-group">';
            data.forEach(function (n) {
                html += '<a class="list-group-item list-group-item-action" href="/Resident/Announcements">' +
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

// The controller the bell talks to: "/Notification" on admin pages, "/Resident" on
// resident pages, "/Home" as the legacy fallback.
function notificationPrefix() {
    var dropdown = document.getElementById('notifDropdown');
    return dropdown && dropdown.dataset.endpointPrefix ? dropdown.dataset.endpointPrefix : '/Home';
}

function notificationEndpoint(action) {
    return notificationPrefix() + '/' + action;
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

// Sidebar document-request badge (the Documents link only). Certificate badges are
// not driven by document requests, so only #documentsSidebarBadge is updated here.
function updateSidebarBadges(count) {
    var remaining = (typeof count === 'number' && count > 0) ? count : 0;

    document.querySelectorAll('#documentsSidebarBadge .sidebar-badge').forEach(function (el) {
        if (remaining <= 0) {
            el.style.display = 'none';
            el.textContent = '0';
        } else {
            el.style.display = '';
            el.textContent = (remaining > 99) ? '99+' : String(remaining);
        }
    });
}

// Removes the inline "NEW" badges from the Documents table - every row when no row is
// given, or just the one that was opened.
function clearInlineNewBadges(row) {
    var scope = row || document;
    scope.querySelectorAll('.doc-new-badge').forEach(function (el) { el.remove(); });
}

// The two bell endpoints answer in two shapes: the admin Home controller returns
// an array, the resident controller returns { unreadCount, notifications }. These
// two helpers let every caller read either one without branching everywhere.
function notificationListFrom(data) {
    if (Array.isArray(data)) return data;
    if (data && Array.isArray(data.notifications)) return data.notifications;
    return [];
}

function notificationCountFrom(data, list) {
    if (data && !Array.isArray(data)) {
        // Home/GetNotifications returns totalBellCount; Resident/GetNotifications
        // returns unreadCount. Either is more authoritative than counting the list.
        if (typeof data.totalBellCount === 'number') return data.totalBellCount;
        if (typeof data.unreadCount === 'number') return data.unreadCount;
    }
    return (list || []).filter(function (n) { return !n.isRead; }).length;
}

function fetchUnreadCount() {
    fetch(notificationEndpoint('GetNotifications'))
        .then(function (r) { return r.json(); })
        .then(function (data) {
            var list = notificationListFrom(data);
            updateNotifBadge(notificationCountFrom(data, list));
        })
        .catch(function (err) { console.error('Unread count fetch failed', err); });
}

// Loads the latest notifications into the open dropdown and syncs the badge.
function fetchNotifications() {
    fetch(notificationEndpoint('GetNotifications'))
        .then(function (r) { return r.json(); })
        .then(function (data) {
            var list = notificationListFrom(data);
            renderNotifDropdown(list);
            // Server count wins: the list is capped, the badge is the full total.
            updateNotifBadge(notificationCountFrom(data, list));
        })
        .catch(function (err) {
            console.error('Failed to load notifications', err);
            var dropdown = document.getElementById('notifDropdown');
            if (dropdown) dropdown.innerHTML = '<div class="notif-empty">Unable to load notifications.</div>';
        });
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
    fetch(notificationEndpoint('GetNotifications'))
        .then(function (r) { return r.json(); })
        .then(function (data) {
            var list = notificationListFrom(data);
            renderNotifDropdown(list);
            updateNotifBadge(notificationCountFrom(data, list));
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

    var html = '<div class="header"><strong>Notifications</strong>' +
        '<button type="button" class="btn btn-link btn-sm text-primary fw-semibold text-decoration-none p-0" onclick="markAllAsReadPermanent(event)">' +
        '<i class="bi bi-check2-all me-1"></i>Mark all as read</button></div>';
    if (!items || items.length === 0) {
        html += '<div class="notif-empty">No notifications at this time.</div>';
        dropdown.innerHTML = html;
        updateNotifBadge(0);
        return;
    }

    html += '<div class="items">';
    items.forEach(function (n) {
        var cls = n.isRead ? 'notif-item read' : 'notif-item unread';
        var type = String(n.type || '').toLowerCase();
        // Green document for approved/ready, red warning for rejected, blue
        // megaphone for announcements; anything else falls back to the document.
        var icon = type.indexOf('announcement') >= 0
            ? '<i class="bi bi-megaphone-fill text-primary"></i>'
            : type.indexOf('reject') >= 0
                ? '<i class="bi bi-exclamation-triangle-fill text-danger"></i>'
                : '<i class="bi bi-file-earmark-check-fill text-success"></i>';
        var targetUrl = n.targetUrl || n.relatedUrl || '';
        html += '<div class="' + cls + '" data-id="' + escapeHtml(n.id) + '"' +
            ' data-type="' + escapeHtml(n.type || '') + '"' +
            ' data-related-url="' + escapeHtml(targetUrl) + '">';
        html += '<div class="notif-icon" aria-hidden="true">' + icon + '</div>';
        html += '<div class="notif-item-content">';
        html += '<div class="title">' + escapeHtml(n.title) + '</div>';
        html += '<div class="meta">' + escapeHtml(n.message || '') + '</div>';
        html += '<div class="meta">' + escapeHtml(n.createdAt || '') + '</div>';
        html += '</div>';
        html += '<button type="button" class="notif-view-btn" data-id="' + escapeHtml(n.id) + '" title="View announcement details" aria-label="View announcement details"><i class="bi bi-eye"></i></button>';
        html += '</div>';
    });
    html += '</div>';

    // Footer points each portal at its own inbox: residents at My Requests,
    // admins at the announcement manager they act on. Rendered as a full-width
    // outline button so it reads as the dropdown's primary call to action.
    var dropdownEl = document.getElementById('notifDropdown');
    var isResident = dropdownEl && dropdownEl.dataset.endpointPrefix === '/Resident';
    html += isResident
        ? '<div class="notif-footer p-2 border-top"><a class="btn btn-sm btn-outline-primary w-100" href="/Resident/Requests">View my requests &rarr;</a></div>'
        : '<div class="notif-footer p-2 border-top"><a class="btn btn-sm btn-outline-primary w-100" href="/Home/Announcements">View all announcements &rarr;</a></div>';

    dropdown.innerHTML = html;

    // Badge is synced by the caller, which has the authoritative unread total.
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

        var badgeEl = document.querySelector('.notif-badge');
        var current = badgeEl ? parseInt(badgeEl.textContent, 10) : NaN;
        if (!isNaN(current) && current > 0) {
            updateNotifBadge(current - 1);
        } else {
            updateNotifBadge(0);
        }
    }

    // Mark as read via POST /<prefix>/MarkAsRead/{id}. Using the id as a route
    // segment (rather than a form field) matches the NotificationController API and
    // still resolves against the resident and home controllers' {id?} routes.
    postJson(notificationEndpoint('MarkAsRead') + '/' + encodeURIComponent(id), '')
    .then(function (r) {
        if (r.status !== 200) throw new Error('MarkAsRead failed with status ' + r.status);
        return r.json();
    })
    .then(function (data) {
        // Server count wins over the optimistic one.
        updateNotifBadge(data && typeof data.unread === 'number' ? data.unread : 0);

        // Refresh the list so the read state on screen matches the database.
        return fetch(notificationEndpoint('GetNotifications'))
            .then(function (r) { return r.json(); })
            .then(function (data) {
                var list = notificationListFrom(data);
                renderNotifDropdown(list);
                updateNotifBadge(notificationCountFrom(data, list));
            });
    })
    .then(function () {
        var item = document.querySelector('.notif-item[data-id="' + CSS.escape(String(id)) + '"]');
        var url = item ? item.getAttribute('data-related-url') : '';
        if (url) {
            window.location.href = url;
        } else {
            openNotificationDetails(id);
        }
    })
    .catch(function (err) {
        console.error(err);
        // Re-sync from the server rather than leaving a badge that may be wrong.
        fetchUnreadCount();
    });
}

function openNotificationDetails(id) {
    fetch(notificationEndpoint('GetNotification') + '?id=' + encodeURIComponent(id))
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

// Server-first "Mark all as read". The badge and rows are only touched once the
// POST confirms the database commit; updating them optimistically beforehand is what
// used to make the red counter blink off and then reappear when the list was re-synced.
function markAllAsReadPermanent(e) {
    if (e) e.stopPropagation();

    // The admin bell posts to /Notification/MarkAllAsRead (NotificationController);
    // the resident bell resolves to /Resident/MarkAllRead through the same prefix.
    var prefix = notificationPrefix();
    var endpoint = (prefix === '/Notification') ? '/Notification/MarkAllAsRead' : prefix + '/MarkAllRead';

    postJson(endpoint, '')
    .then(function (r) {
        if (r.status !== 200) throw new Error('Mark all read failed with status ' + r.status);
        return r.json();
    })
    .then(function (data) {
        // Commit confirmed - only now update the DOM. Every unread row flips to its
        // read appearance and the badge is set from the server's authoritative count.
        var dropdown = document.getElementById('notifDropdown');
        if (dropdown) {
            dropdown.querySelectorAll('.notif-item.unread').forEach(function (item) {
                item.classList.remove('unread');
                item.classList.add('read');
            });
        }
        updateNotifBadge(data && typeof data.unread === 'number' ? data.unread : 0);

        // A single "Mark all as read" also acknowledges the document requests, so the
        // sidebar counters and the inline NEW badges are cleared here rather than
        // waiting for the next page load.
        updateSidebarBadges(0);
        clearInlineNewBadges();

        // Re-read the list so its read state matches the database.
        return fetch(notificationEndpoint('GetNotifications'))
            .then(function (r) { return r.json(); })
            .then(function (resp) {
                var list = notificationListFrom(resp);
                renderNotifDropdown(list);
                updateNotifBadge(notificationCountFrom(resp, list));
            });
    })
    .catch(function (err) {
        console.error('Mark all read failed', err);
        // No commit, so leave the UI as-is and resync from the server rather than
        // hiding a badge that is still set.
        fetchUnreadCount();
    });
}

// Backwards-compatible alias: any markup still wired to markAllRead() keeps working.
function markAllRead(e) {
    return markAllAsReadPermanent(e);
}

// ensure unread count is fetched when page loads, and wire up notification item clicks
document.addEventListener('DOMContentLoaded', function () {
    fetchUnreadCount();

    // Resident bell: Bootstrap drives the open/close state, so the list is loaded
    // when the dropdown is about to show. Admin pages keep using toggleNotifDropdown.
    document.addEventListener('show.bs.dropdown', function (ev) {
        var toggle = ev.target;
        if (toggle && toggle.closest && toggle.closest('.notification-wrapper')) {
            fetchNotifications();
        }
    });

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

    // Announcement row delete is handled in Announcements.cshtml, which owns the
    // in-place row removal and the summary-card counts. The handler that used to
    // live here targeted a .announcement-delete-btn class that no longer exists
    // anywhere, and it answered by reloading the whole page, so it was dead code
    // and a hazard if a matching class ever returned.
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
                    // Icon-only, same .ann-actions/.icon-btn-case outline styling
                    // as the server-rendered rows in Announcements.cshtml, so a row
                    // added through this path is indistinguishable from the rest.
                    "<td class=\"ann-action-cell\"><div class=\"action-buttons ann-actions\">" +
                    "<button type=\"button\" class=\"icon-btn-case icon-edit\" title=\"Edit\" aria-label=\"Edit announcement\" data-edit-id=\"" + escapeHtml(item.id) + "\"><i class=\"bi bi-pencil\"></i></button>" +
                    "<button type=\"button\" class=\"icon-btn-case icon-delete\" title=\"Delete\" aria-label=\"Delete announcement\" data-delete-id=\"" + escapeHtml(item.id) + "\"><i class=\"bi bi-trash\"></i></button>" +
                    "</div></td>";

                // prepend new row
                if (tbody.firstChild) tbody.insertBefore(tr, tbody.firstChild);
                else tbody.appendChild(tr);

                // New rows use inline <i class="bi"> icons (Bootstrap Icons load with
                // the layout), so no lucide refresh pass is needed here.
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
                fetch(notificationEndpoint('GetNotifications')).then(function (r) { return r.json(); }).then(function (items) { renderNotifDropdown(items || []); });
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
