document.addEventListener("DOMContentLoaded", () => {
  // --- Theme functionality has been removed to enforce dark mode ---

  // --- Sidebar Toggle for Mobile ---
  const sidebar = document.getElementById("sidebar");
  const openSidebarBtn = document.getElementById("open-sidebar-btn");
  const closeSidebarBtn = document.getElementById("close-sidebar-btn");
  const navLinks = document.querySelectorAll("#nav-menu a");

  openSidebarBtn.addEventListener("click", () => sidebar.classList.add("open"));
  closeSidebarBtn.addEventListener("click", () =>
    sidebar.classList.remove("open")
  );
  navLinks.forEach((link) => {
    link.addEventListener("click", () => {
      if (window.innerWidth < 1024) {
        sidebar.classList.remove("open");
      }
    });
  });

  // --- Header Title Update on Scroll ---
  const headerTitle = document.getElementById("header-title");
  const sections = document.querySelectorAll("main section");
  const observer = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          const sectionTitle = entry.target.querySelector("h2").textContent;
          headerTitle.textContent = sectionTitle;
        }
      });
    },
    { rootMargin: "-20% 0px -80% 0px" }
  );

  sections.forEach((section) => {
    observer.observe(section);
  });

  // --- Active Nav Link Highlighting ---
  const navMenuLinks = document.querySelectorAll("#nav-menu a");
  const navObserver = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          const id = entry.target.getAttribute("id");
          navMenuLinks.forEach((link) => {
            link.classList.remove(
              "bg-gray-200",
              "dark:bg-gray-700",
              "font-semibold"
            );
            if (link.getAttribute("href") === `#${id}`) {
              link.classList.add(
                "bg-gray-200",
                "dark:bg-gray-700",
                "font-semibold"
              );
            }
          });
        }
      });
    },
    { rootMargin: "-40% 0px 0px 0px" } // Adjusted rootMargin here
  );

  sections.forEach((section) => {
    navObserver.observe(section);
  });

  // --- Search Functionality ---
  const searchBar = document.getElementById("search-bar");
  const allSections = Array.from(document.querySelectorAll("main section"));
  const allNavLinks = Array.from(document.querySelectorAll("#nav-menu li"));

  searchBar.addEventListener("input", (e) => {
    const query = e.target.value.toLowerCase();

    // Filter sections in main content
    allSections.forEach((section) => {
      const title = section.querySelector("h2").textContent.toLowerCase();
      const content = section.textContent.toLowerCase();
      if (title.includes(query) || content.includes(query)) {
        section.style.display = "block";
      } else {
        section.style.display = "none";
      }
    });

    // Filter nav links
    allNavLinks.forEach((li) => {
      const link = li.querySelector("a");
      const linkText = link.textContent.toLowerCase();
      if (linkText.includes(query)) {
        li.style.display = "block";
      } else {
        li.style.display = "none";
      }
    });
  });

  // --- Copy Code Button Functionality ---
  const codeContainers = document.querySelectorAll(".code-container");
  codeContainers.forEach((container) => {
    const codeBlock = container.querySelector("pre code");
    const copyButton = document.createElement("button");
    copyButton.className = "copy-btn";
    copyButton.innerHTML = `<svg class="w-4 h-4 mr-1 inline-block" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M8 16H6a2 2 0 01-2-2V6a2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"></path></svg> Copy`;
    container.appendChild(copyButton);

    copyButton.addEventListener("click", () => {
      const codeToCopy = codeBlock.textContent;
      const textArea = document.createElement("textarea");
      textArea.value = codeToCopy;
      document.body.appendChild(textArea);
      textArea.select();
      try {
        document.execCommand("copy");
        copyButton.innerHTML = `<svg class="w-4 h-4 mr-1 inline-block" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7"></path></svg> Copied!`;
      } catch (err) {
        console.error("Failed to copy text: ", err);
        copyButton.textContent = "Error";
      }
      document.body.removeChild(textArea);

      setTimeout(() => {
        copyButton.innerHTML = `<svg class="w-4 h-4 mr-1 inline-block" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"></path></svg> Copy`;
      }, 2000);
    });
  });
});
