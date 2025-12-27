// CORRECTED MongoDB query - handles null post_list correctly
// This version explicitly checks for null and empty arrays

db.followings.aggregate([
  {
    $project: {
      following_username: 1,
      postListType: { $type: "$response_data.data.post_list" },
      postListSize: {
        $cond: {
          if: {
            $and: [
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
      username: "$following_username",
      postListType: 1,
      postListSize: 1,
      postsCount: { $size: "$posts" }
    }
  }
])

// EVEN SIMPLER VERSION - Just check if post_list is null or empty array
db.followings.aggregate([
  {
    $addFields: {
      postListIsEmpty: {
        $or: [
          { $eq: ["$response_data.data.post_list", null] },
          { $eq: [{ $type: "$response_data.data.post_list" }, "null"] },
          {
            $and: [
              { $eq: [{ $type: "$response_data.data.post_list" }, "array"] },
              { $eq: [{ $size: "$response_data.data.post_list" }, 0] }
            ]
          }
        ]
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
        { postListIsEmpty: true }
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

// DEBUGGING VERSION - See what's happening with _aaashiiii_
db.followings.aggregate([
  {
    $match: {
      following_username: "_aaashiiii_"
    }
  },
  {
    $project: {
      following_username: 1,
      postListValue: "$response_data.data.post_list",
      postListType: { $type: "$response_data.data.post_list" },
      postListIsNull: { $eq: ["$response_data.data.post_list", null] },
      postListSize: {
        $cond: {
          if: { $eq: [{ $type: "$response_data.data.post_list" }, "array"] },
          then: { $size: "$response_data.data.post_list" },
          else: "not_an_array"
        }
      }
    }
  }
])


