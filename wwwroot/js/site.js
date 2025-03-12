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

});

document.addEventListener('DOMContentLoaded', function () {
    // Check if the current page is a settings subpage
    const controller = document.body.getAttribute('data-controller');
    const action = document.body.getAttribute('data-action');

    // If we're on a System controller page, expand the settings accordion
    if (controller === 'System') {
        const settingsCollapse = document.getElementById('settingsCollapse');
        if (settingsCollapse) {
            const bsCollapse = new bootstrap.Collapse(settingsCollapse, {
                toggle: false
            });
            bsCollapse.show();
        }
    }
});