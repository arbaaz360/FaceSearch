function Pagination({ skip, take, total, onPageChange }) {
  const currentPage = Math.floor(skip / take) + 1
  const totalPages = Math.ceil(total / take)
  const startItem = skip + 1
  const endItem = Math.min(skip + take, total)

  if (total <= take) {
    return null // Don't show pagination if all items fit on one page
  }

  const goToPage = (page) => {
    const newSkip = (page - 1) * take
    onPageChange(newSkip)
  }

  const goToFirst = () => goToPage(1)
  const goToPrevious = () => goToPage(currentPage - 1)
  const goToNext = () => goToPage(currentPage + 1)
  const goToLast = () => goToPage(totalPages)

  // Calculate page numbers to show
  const getPageNumbers = () => {
    const pages = []
    const maxPagesToShow = 7
    
    if (totalPages <= maxPagesToShow) {
      // Show all pages
      for (let i = 1; i <= totalPages; i++) {
        pages.push(i)
      }
    } else {
      // Show first page, last page, current page, and pages around current
      pages.push(1)
      
      if (currentPage > 3) {
        pages.push('...')
      }
      
      const start = Math.max(2, currentPage - 1)
      const end = Math.min(totalPages - 1, currentPage + 1)
      
      for (let i = start; i <= end; i++) {
        pages.push(i)
      }
      
      if (currentPage < totalPages - 2) {
        pages.push('...')
      }
      
      pages.push(totalPages)
    }
    
    return pages
  }

  return (
    <div style={{ 
      display: 'flex', 
      justifyContent: 'center', 
      alignItems: 'center', 
      gap: '8px', 
      marginTop: '24px',
      flexWrap: 'wrap'
    }}>
      <button
        className="btn btn-secondary"
        onClick={goToFirst}
        disabled={currentPage === 1}
        title="First page"
      >
        ««
      </button>
      <button
        className="btn btn-secondary"
        onClick={goToPrevious}
        disabled={currentPage === 1}
        title="Previous page"
      >
        ‹ Previous
      </button>
      
      <div style={{ display: 'flex', gap: '4px', alignItems: 'center' }}>
        {getPageNumbers().map((page, idx) => {
          if (page === '...') {
            return (
              <span key={`ellipsis-${idx}`} style={{ padding: '0 8px', color: 'var(--muted)' }}>
                ...
              </span>
            )
          }
          return (
            <button
              key={page}
              className={`btn ${page === currentPage ? 'btn-primary' : 'btn-secondary'}`}
              onClick={() => goToPage(page)}
              style={{ minWidth: '40px' }}
            >
              {page}
            </button>
          )
        })}
      </div>
      
      <button
        className="btn btn-secondary"
        onClick={goToNext}
        disabled={currentPage === totalPages}
        title="Next page"
      >
        Next ›
      </button>
      <button
        className="btn btn-secondary"
        onClick={goToLast}
        disabled={currentPage === totalPages}
        title="Last page"
      >
        »»
      </button>
      
      <span style={{ 
        marginLeft: '16px', 
        color: 'var(--muted)', 
        fontSize: '14px' 
      }}>
        Showing {startItem}-{endItem} of {total}
      </span>
    </div>
  )
}

export default Pagination

