// Theme switcher functionality - Pink Theme version
document.addEventListener('DOMContentLoaded', function () {
    // Get the theme toggle element
    const themeToggle = document.getElementById('theme-toggle');
    const themeIcon = document.getElementById('theme-icon');

    // Check if user has already selected a theme
    const currentTheme = localStorage.getItem('theme') || 'light';

    // Apply the theme on initial load
    document.documentElement.setAttribute('data-bs-theme', currentTheme);

    // Update the toggle icon based on current theme
    updateThemeIcon(currentTheme);

    // Add event listener to the toggle button
    if (themeToggle) {
        themeToggle.addEventListener('click', function () {
            // Get current theme
            const currentTheme = document.documentElement.getAttribute('data-bs-theme');
            // Switch theme
            const newTheme = currentTheme === 'dark' ? 'light' : 'dark';

            // Apply the new theme
            document.documentElement.setAttribute('data-bs-theme', newTheme);

            // Save theme preference to localStorage
            localStorage.setItem('theme', newTheme);

            // Update the icon
            updateThemeIcon(newTheme);
        });
    }

    // Function to update the theme icon
    function updateThemeIcon(theme) {
        if (!themeIcon) return;

        if (theme === 'dark') {
            themeIcon.classList.remove('bi-brightness-high');
            themeIcon.classList.add('bi-moon-stars');
        } else {
            themeIcon.classList.remove('bi-moon-stars');
            themeIcon.classList.add('bi-brightness-high');
        }
    }

    // Function to apply the pink monochromatic theme
    function applyPinkTheme() {
        // The pink theme is now applied via CSS variables in theme.css
        // This function exists for potential future dynamic theme adjustments
    }

    // Apply the pink theme (on top of light/dark preference)
    applyPinkTheme();

    // Auto-refresh functionality for donation and device pages
    initializeAutoRefresh();
});

// Function to handle auto-refresh for donation and device pages
function initializeAutoRefresh() {
    // Check if we're on a page that needs auto-refresh
    const donationListContainer = document.getElementById('donationListContainer');
    const deviceListContainer = document.getElementById('deviceListContainer');

    if (!donationListContainer && !deviceListContainer) return;

    // Variables for auto-refresh
    const refreshInterval = 10 * 1000; // 10 seconds
    let autoRefreshTimer;
    let secondsCounter;
    let secondsLeft = refreshInterval / 1000;

    const refreshStatus = document.getElementById('refreshStatus');
    const lastRefreshTime = document.getElementById('lastRefreshTime');
    const manualRefresh = document.getElementById('manualRefresh');
    const autoRefreshToggle = document.getElementById('autoRefreshToggle');

    // Function to update last refresh time
    function updateLastRefreshTime() {
        if (!lastRefreshTime) return;

        const now = new Date();
        const hours = String(now.getHours()).padStart(2, '0');
        const minutes = String(now.getMinutes()).padStart(2, '0');
        const seconds = String(now.getSeconds()).padStart(2, '0');
        lastRefreshTime.textContent = `${hours}:${minutes}:${seconds}`;

        // Reset countdown
        secondsLeft = refreshInterval / 1000;
        updateRefreshStatus();
    }

    // Function to update refresh status text with countdown
    function updateRefreshStatus() {
        if (!refreshStatus || !lastRefreshTime) return;

        refreshStatus.innerHTML = `<i class="bi bi-arrow-clockwise"></i> Auto-refreshing in ${secondsLeft} seconds | Last update: <span id="lastRefreshTime">${lastRefreshTime.textContent}</span>`;
    }

    // Function to refresh the list
    function refreshList() {
        // Determine which list to refresh
        const listContainer = donationListContainer || deviceListContainer;
        const controller = donationListContainer ? 'Donations' : 'Devices';

        // Make the AJAX call
        fetch(`/${controller}/Index`, {
            headers: {
                'X-Requested-With': 'XMLHttpRequest'
            }
        })
            .then(response => response.text())
            .then(html => {
                listContainer.innerHTML = html;
                updateLastRefreshTime();
            })
            .catch(error => {
                console.error(`Error refreshing ${controller} list:`, error);
            });
    }

    // Start auto-refresh timer
    function startAutoRefresh() {
        // Clear any existing timers
        stopAutoRefresh();

        // Start new timer
        autoRefreshTimer = setInterval(refreshList, refreshInterval);

        // Start the countdown update
        secondsLeft = refreshInterval / 1000;
        secondsCounter = setInterval(function () {
            secondsLeft--;
            updateRefreshStatus();
        }, 1000);

        if (autoRefreshToggle) {
            autoRefreshToggle.checked = true;
        }
    }

    // Stop auto-refresh timer
    function stopAutoRefresh() {
        clearInterval(autoRefreshTimer);
        clearInterval(secondsCounter);

        if (refreshStatus && lastRefreshTime) {
            refreshStatus.innerHTML = `<i class="bi bi-clock-history"></i> Auto-refresh paused | Last update: <span id="lastRefreshTime">${lastRefreshTime.textContent}</span>`;
        }

        if (autoRefreshToggle) {
            autoRefreshToggle.checked = false;
        }
    }

    // Setup event listeners
    if (manualRefresh) {
        manualRefresh.addEventListener('click', function () {
            refreshList();

            // If auto-refresh is on, restart the timer
            if (autoRefreshToggle && autoRefreshToggle.checked) {
                startAutoRefresh();
            }
        });
    }

    if (autoRefreshToggle) {
        autoRefreshToggle.addEventListener('change', function () {
            if (this.checked) {
                startAutoRefresh();
            } else {
                stopAutoRefresh();
            }
        });
    }

    // Initialize auto-refresh
    startAutoRefresh();
}