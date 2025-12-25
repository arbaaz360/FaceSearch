// CORRECTED MongoDB query to find usernames without posts
// This properly handles null post_list values

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

// Alternative simpler version (if you just want to check if post_list exists and is an array with items)
db.followings.aggregate([
  {
    $project: {
      following_username: 1,
      hasNestedPosts: {
        $cond: {
          if: {
            $and: [
              { $ne: ["$response_data.data.post_list", null] },
              { $eq: [{ $type: "$response_data.data.post_list" }, "array"] },
              { $gt: [{ $size: "$response_data.data.post_list" }, 0] }
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

