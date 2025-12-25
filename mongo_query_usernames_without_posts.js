// MongoDB query to find usernames that don't have posts
// Run this in MongoDB shell or Compass

// Database: instafollowing
// Collections: followings, posts

// Method 1: Using aggregation to find usernames without posts in the 'posts' collection
db.followings.aggregate([
  {
    $project: {
      following_username: 1
    }
  },
  {
    $lookup: {
      from: "posts",
      localField: "following_username",
      foreignField: "following_username",
      as: "posts"
    }
  },
  {
    $match: {
      posts: { $size: 0 }  // No posts found
    }
  },
  {
    $count: "usernames_without_posts"
  }
])

// Method 2: Also check if posts exist in nested structure within followings collection
db.followings.aggregate([
  {
    $project: {
      following_username: 1,
      hasNestedPosts: {
        $cond: {
          if: {
            $and: [
              { $ne: ["$response_data", null] },
              { $ne: ["$response_data.data", null] },
              { $ne: ["$response_data.data.post_list", null] },
              { $gt: [{ $size: { $ifNull: ["$response_data.data.post_list", []] } }, 0] }
            ]
          },
          then: true,
          else: false
        }
      }
    }
  },
  {
    $lookup: {
      from: "posts",
      localField: "following_username",
      foreignField: "following_username",
      as: "posts"
    }
  },
  {
    $match: {
      $and: [
        { posts: { $size: 0 } },  // No posts in posts collection
        { hasNestedPosts: false }  // No nested posts in followings collection
      ]
    }
  },
  {
    $count: "usernames_without_posts"
  }
])

// Method 3: Get the actual list of usernames without posts (not just count)
// FIXED: Properly handles null post_list and empty arrays
db.followings.aggregate([
  {
    $project: {
      following_username: 1,
      hasNestedPosts: {
        $cond: {
          if: {
            $and: [
              { $ne: ["$response_data", null] },
              { $ne: ["$response_data.data", null] },
              { 
                $and: [
                  { $ne: ["$response_data.data.post_list", null] },
                  { $ne: [{ $type: "$response_data.data.post_list" }, "null"] },
                  { $eq: [{ $type: "$response_data.data.post_list" }, "array"] },
                  { $gt: [{ $size: "$response_data.data.post_list" }, 0] }
                ]
              }
            ]
          },
          then: true,
          else: false
        }
      }
    }
  },
  {
    $lookup: {
      from: "posts",
      localField: "following_username",
      foreignField: "following_username",
      as: "posts"
    }
  },
  {
    $match: {
      $and: [
        { posts: { $size: 0 } },
        { hasNestedPosts: false }
      ]
    }
  },
  {
    $project: {
      _id: 0,
      username: "$following_username"
    }
  }
])

// CORRECTED VERSION - Simpler and more reliable
db.followings.aggregate([
  {
    $project: {
      following_username: 1,
      postListSize: {
        $cond: {
          if: {
            $and: [
              { $ne: ["$response_data", null] },
              { $ne: ["$response_data.data", null] },
              { $ne: ["$response_data.data.post_list", null] },
              { $eq: [{ $type: "$response_data.data.post_list" }, "array"] }
            ]
          },
          then: { $size: "$response_data.data.post_list" },
          else: 0
        }
      }
    }
  },
  {
    $lookup: {
      from: "posts",
      localField: "following_username",
      foreignField: "following_username",
      as: "posts"
    }
  },
  {
    $match: {
      $and: [
        { posts: { $size: 0 } },
        { postListSize: 0 }
      ]
    }
  },
  {
    $project: {
      _id: 0,
      username: "$following_username"
    }
  }
])

// Method 4: Simple count using distinct and lookup (fastest for just count)
db.followings.distinct("following_username").length - db.posts.distinct("following_username").length

// Method 5: More accurate - check both collections and nested posts
db.followings.aggregate([
  {
    $group: {
      _id: "$following_username"
    }
  },
  {
    $lookup: {
      from: "posts",
      localField: "_id",
      foreignField: "following_username",
      as: "posts"
    }
  },
  {
    $lookup: {
      from: "followings",
      let: { username: "$_id" },
      pipeline: [
        {
          $match: {
            $expr: {
              $and: [
                { $eq: ["$following_username", "$$username"] },
                { $ne: ["$response_data.data.post_list", null] },
                { $gt: [{ $size: { $ifNull: ["$response_data.data.post_list", []] } }, 0] }
              ]
            }
          }
        }
      ],
      as: "nestedPosts"
    }
  },
  {
    $match: {
      $and: [
        { posts: { $size: 0 } },
        { nestedPosts: { $size: 0 } }
      ]
    }
  },
  {
    $count: "usernames_without_posts"
  }
])

