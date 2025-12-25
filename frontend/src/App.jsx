import { BrowserRouter, Routes, Route, Link } from 'react-router-dom'
import Albums from './pages/Albums'
import AlbumDetail from './pages/AlbumDetail'
import Search from './pages/Search'
import FaceReview from './pages/FaceReview'
import Reviews from './pages/Reviews'
import Diagnostics from './pages/Diagnostics'
import Indexing from './pages/Indexing'
import './App.css'

function App() {
  return (
    <BrowserRouter>
      <div className="app">
        <nav className="nav">
          <div className="nav-content">
            <Link to="/" className="nav-logo">
              FaceSearch
            </Link>
            <div className="nav-links">
              <Link to="/albums">Albums</Link>
              <Link to="/search">Search</Link>
              <Link to="/face-review">Face Review</Link>
              <Link to="/reviews">Reviews</Link>
              <Link to="/indexing">Indexing</Link>
              <Link to="/diagnostics">Diagnostics</Link>
            </div>
          </div>
        </nav>

        <main className="main">
          <Routes>
            <Route path="/" element={<Albums />} />
            <Route path="/albums" element={<Albums />} />
            <Route path="/albums/:albumId" element={<AlbumDetail />} />
            <Route path="/search" element={<Search />} />
            <Route path="/face-review" element={<FaceReview />} />
            <Route path="/reviews" element={<Reviews />} />
            <Route path="/indexing" element={<Indexing />} />
            <Route path="/diagnostics" element={<Diagnostics />} />
          </Routes>
        </main>
      </div>
    </BrowserRouter>
  )
}

export default App

